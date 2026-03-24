using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace VsAgentic.Services.Services;

public class ChatService(
    AnthropicHttpClient httpClient,
    IModelRouter modelRouter,
    IOptions<VsAgenticOptions> options,
    IEnumerable<ToolDefinition> tools,
    IOutputListener outputListener,
    ResiliencePipeline resiliencePipeline,
    ILogger<ChatService> logger) : IChatService
{
    private readonly VsAgenticOptions _options = options.Value;
    private readonly List<Message> _history = [];
    private readonly List<ToolDefinition> _tools = tools.ToList();

    public ModelMode ModelMode
    {
        get => modelRouter.Mode;
        set => modelRouter.Mode = value;
    }

    public async IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Add user message to history
        _history.Add(new Message { Role = "user", Content = userMessage });

        // Count user turns for conversation depth (used by Auto routing)
        var conversationDepth = _history.Count(m => m.Role == "user");
        var modelId = await modelRouter.ResolveModelAsync(userMessage, conversationDepth, cancellationToken);
        var enableThinking = modelId != ModelIds.Haiku;

        logger.LogDebug("Sending message to Claude ({Model}) with {ToolCount} tools available", modelId, _tools.Count);

        var channel = Channel.CreateUnbounded<string>();

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await resiliencePipeline.ExecuteAsync(async ct =>
                {
                    OutputItem? thinkingItem = null;
                    var thinkingBuilder = new StringBuilder();
                    DateTime? thinkingStartTime = null;

                    OutputItem? responseItem = null;
                    var responseBuilder = new StringBuilder();

                    var engine = new ChatEngine(httpClient, logger);
                    var callbacks = new ChatEngine.Callbacks
                    {
                        OnTextDelta = text =>
                        {
                            // Close thinking block if open
                            if (thinkingItem is not null)
                            {
                                var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                                thinkingItem.Status = OutputItemStatus.Success;
                                thinkingItem.Title = $"Thought for {elapsed}s";
                                thinkingItem.Delta = null;
                                outputListener.OnStepCompleted(thinkingItem);
                                thinkingItem = null;
                                thinkingBuilder.Clear();
                            }

                            // Start response block if needed
                            if (responseItem is null)
                            {
                                responseItem = new OutputItem
                                {
                                    Id = Guid.NewGuid().ToString("N"),
                                    ToolName = "AI",
                                    Title = "Responding",
                                    Status = OutputItemStatus.Pending
                                };
                                outputListener.OnStepStarted(responseItem);
                            }

                            responseBuilder.Append(text);
                            responseItem.Delta = text;
                            responseItem.Body = responseBuilder.ToString();
                            outputListener.OnStepUpdated(responseItem);

                            channel.Writer.TryWrite(text);
                        },

                        OnThinkingDelta = thinking =>
                        {
                            // Close response block if open (interleaved thinking)
                            if (responseItem is not null)
                            {
                                responseItem.Status = OutputItemStatus.Success;
                                responseItem.Delta = null;
                                outputListener.OnStepCompleted(responseItem);
                                responseItem = null;
                                responseBuilder.Clear();
                            }

                            if (thinkingItem is null)
                            {
                                thinkingStartTime = DateTime.UtcNow;
                                thinkingItem = new OutputItem
                                {
                                    Id = Guid.NewGuid().ToString("N"),
                                    ToolName = "Thinking",
                                    Title = "Thinking...",
                                    Status = OutputItemStatus.Pending
                                };
                                outputListener.OnStepStarted(thinkingItem);
                            }

                            thinkingBuilder.Append(thinking);
                            var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                            thinkingItem.Delta = thinking;
                            thinkingItem.Body = thinkingBuilder.ToString();
                            thinkingItem.Title = elapsed > 0 ? $"Thought for {elapsed}s" : "Thinking...";
                            outputListener.OnStepUpdated(thinkingItem);
                        },

                        OnToolCallStarted = (toolName, toolUseId) =>
                        {
                            // Close response/thinking blocks
                            if (responseItem is not null)
                            {
                                responseItem.Status = OutputItemStatus.Success;
                                responseItem.Delta = null;
                                outputListener.OnStepCompleted(responseItem);
                                responseItem = null;
                                responseBuilder.Clear();
                            }
                            if (thinkingItem is not null)
                            {
                                var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                                thinkingItem.Status = OutputItemStatus.Success;
                                thinkingItem.Title = $"Thought for {elapsed}s";
                                thinkingItem.Delta = null;
                                outputListener.OnStepCompleted(thinkingItem);
                                thinkingItem = null;
                                thinkingBuilder.Clear();
                            }
                        }
                    };

                    await engine.RunAsync(modelId, _options.SystemPrompt, _history, _tools, enableThinking, callbacks, ct);

                    // Close any remaining blocks
                    if (thinkingItem is not null)
                    {
                        var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                        thinkingItem.Status = OutputItemStatus.Success;
                        thinkingItem.Title = $"Thought for {elapsed}s";
                        thinkingItem.Delta = null;
                        outputListener.OnStepCompleted(thinkingItem);
                    }
                    if (responseItem is not null)
                    {
                        responseItem.Status = OutputItemStatus.Success;
                        responseItem.Title = "Response complete";
                        responseItem.Delta = null;
                        outputListener.OnStepCompleted(responseItem);
                    }
                }, cancellationToken);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var text in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return text;
        }

        await producerTask;
    }

    public Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default)
        => modelRouter.GenerateTitleAsync(userMessage, cancellationToken);

    public void ClearHistory()
    {
        _history.Clear();
        modelRouter.ResetAutoLock();
        logger.LogInformation("Conversation history cleared");
    }

    public string SerializeHistory()
    {
        return JsonSerializer.Serialize(_history, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public void RestoreHistory(string serializedHistory)
    {
        var messages = JsonSerializer.Deserialize<List<Message>>(serializedHistory, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (messages is null) return;

        _history.Clear();
        _history.AddRange(messages);

        logger.LogInformation("Restored conversation history with {Count} messages", _history.Count);
    }
}
