using System.Text;
using Anthropic.SDK.Constants;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace VsAgentic.Services.Services;

public class AgentToolService(
    IChatClient chatClient,
    [FromKeyedServices("base")] IEnumerable<AITool> baseTools,
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

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, composedPrompt),
            new(ChatRole.User, task)
        };

        var chatOptions = new ChatOptions
        {
            Tools = baseTools.ToList(),
            ModelId = AnthropicModels.Claude45Haiku
        };

        logger.LogDebug("Agent starting task with {ToolCount} tools: {Task}", baseTools.Count(), task);

        try
        {
            var updates = new List<ChatResponseUpdate>();
            var fullResponseBuilder = new StringBuilder();

            await resiliencePipeline.ExecuteAsync(async ct =>
            {
                OutputItem? responseItem = null;
                var responseBuilder = new StringBuilder();

                await foreach (var update in chatClient.GetStreamingResponseAsync(history, chatOptions, ct))
                {
                    updates.Add(update);

                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent or FunctionResultContent)
                        {
                            if (responseItem is not null)
                            {
                                responseItem.Status = OutputItemStatus.Success;
                                responseItem.Delta = null;
                                outputListener.OnStepCompleted(responseItem);
                                responseItem = null;
                                responseBuilder.Clear();
                            }

                            continue;
                        }

                        if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
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

                            responseBuilder.Append(textContent.Text);
                            fullResponseBuilder.Append(textContent.Text);
                            responseItem.Delta = textContent.Text;
                            responseItem.Body = responseBuilder.ToString();
                            outputListener.OnStepUpdated(responseItem);
                        }
                    }
                }

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
