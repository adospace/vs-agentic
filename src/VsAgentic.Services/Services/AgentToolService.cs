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

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Agent",
            Title = $"Agent {skill}",
            Body = systemPrompt,
            Status = OutputItemStatus.Pending
        };
        outputListener.OnStepStarted(item);

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
            var responseBuilder = new StringBuilder();

            await resiliencePipeline.ExecuteAsync(async ct =>
            {
                await foreach (var update in chatClient.GetStreamingResponseAsync(history, chatOptions, ct))
                {
                    updates.Add(update);

                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        responseBuilder.Append(text);
                        item.Delta = text;
                        item.Body = responseBuilder.ToString();
                        item.Title = "Agent responding...";
                        outputListener.OnStepUpdated(item);
                    }
                }
            }, cancellationToken);

            var result = responseBuilder.Length > 0 ? responseBuilder.ToString() : "[no response]";

            item.Status = OutputItemStatus.Success;
            item.Title = "Agent complete";
            item.Body = result.Length > 500 ? result[..500] + "..." : result;
            item.Delta = null;
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Agent completed task. Response length: {Length}", result.Length);

            return result;
        }
        catch (Exception ex)
        {
            item.Status = OutputItemStatus.Error;
            item.Title = "Agent failed";
            item.Body = ex.Message;
            outputListener.OnStepCompleted(item);

            logger.LogError(ex, "Agent task failed");
            return $"[Agent error: {ex.Message}]";
        }
    }
}
