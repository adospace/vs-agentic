using System.Text;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace VsAgentic.Services.Services;

public class AgentToolService(
    AnthropicHttpClient httpClient,
    [FromKeyedServices("base")] IEnumerable<ToolDefinition> baseTools,
    IOptions<VsAgenticOptions> options,
    IOutputListener outputListener,
    ResiliencePipeline resiliencePipeline,
    ILogger<AgentToolService> logger) : IAgentToolService
{
    private readonly VsAgenticOptions _options = options.Value;

    public async Task<string> RunAsync(string task, string systemPrompt, string skill = "generic", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(task));
        if (string.IsNullOrWhiteSpace(systemPrompt)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(systemPrompt));

        logger.LogTrace("[Agent] Args received — task: {Task}, skill: {Skill}", task, skill);

        var composedPrompt = $"{_options.AgentSystemPrompt}\n\n# Your Role\n{systemPrompt}";

        var agentItem = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Agent",
            Title = $"Agent {skill}",
            Body = task,
            Status = OutputItemStatus.Pending
        };
        outputListener.OnStepStarted(agentItem);

        var history = new List<Message>
        {
            new() { Role = "user", Content = task }
        };

        var toolList = baseTools.ToList();

        logger.LogDebug("Agent starting task with {ToolCount} tools: {Task}", toolList.Count, task);

        try
        {
            var fullResponseBuilder = new StringBuilder();

            await resiliencePipeline.ExecuteAsync(async ct =>
            {
                OutputItem? responseItem = null;
                var responseBuilder = new StringBuilder();

                var engine = new ChatEngine(httpClient, logger);
                var callbacks = new ChatEngine.Callbacks
                {
                    OnTextDelta = text =>
                    {
                        if (responseItem is null)
                        {
                            responseItem = new OutputItem
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                ToolName = "AI",
                                Title = "Agent responding...",
                                Status = OutputItemStatus.Pending
                            };
                            outputListener.OnStepStarted(responseItem);
                        }

                        responseBuilder.Append(text);
                        fullResponseBuilder.Append(text);
                        responseItem.Delta = text;
                        responseItem.Body = responseBuilder.ToString();
                        outputListener.OnStepUpdated(responseItem);
                    },

                    OnToolCallStarted = (toolName, toolUseId) =>
                    {
                        if (responseItem is not null)
                        {
                            responseItem.Status = OutputItemStatus.Success;
                            responseItem.Delta = null;
                            outputListener.OnStepCompleted(responseItem);
                            responseItem = null;
                            responseBuilder.Clear();
                        }
                    }
                };

                await engine.RunAsync(
                    ModelIds.Haiku,
                    composedPrompt,
                    history,
                    toolList,
                    enableThinking: false,
                    callbacks,
                    ct);

                if (responseItem is not null)
                {
                    responseItem.Status = OutputItemStatus.Success;
                    responseItem.Delta = null;
                    outputListener.OnStepCompleted(responseItem);
                }
            }, cancellationToken);

            var result = fullResponseBuilder.Length > 0 ? fullResponseBuilder.ToString() : "[no response]";

            agentItem.Status = OutputItemStatus.Success;
            agentItem.Title = $"Agent {skill} complete";
            agentItem.Delta = null;
            outputListener.OnStepCompleted(agentItem);

            logger.LogDebug("Agent completed task. Response length: {Length}", result.Length);

            return result;
        }
        catch (Exception ex)
        {
            agentItem.Status = OutputItemStatus.Error;
            agentItem.Title = "Agent failed";
            agentItem.Body = ex.Message;
            outputListener.OnStepCompleted(agentItem);

            logger.LogError(ex, "Agent task failed");
            return $"[Agent error: {ex.Message}]";
        }
    }
}
