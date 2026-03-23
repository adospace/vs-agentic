using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Extensions;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace VsAgentic.Services.Services;

public class ChatService(
    IChatClient chatClient,
    IModelRouter modelRouter,
    IOptions<VsAgenticOptions> options,
    IEnumerable<AITool> tools,
    IOutputListener outputListener,
    ResiliencePipeline resiliencePipeline,
    ILogger<ChatService> logger) : IChatService
{
    private readonly VsAgenticOptions _options = options.Value;
    private readonly List<ChatMessage> _history = [];
    private readonly List<AITool> _tools = tools.ToList();

    public ModelMode ModelMode
    {
        get => modelRouter.Mode;
        set => modelRouter.Mode = value;
    }

    public async IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_history.Count == 0)
        {
            _history.Add(new ChatMessage(ChatRole.System, _options.SystemPrompt));
        }

        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        // Count user turns for conversation depth (used by Auto routing)
        var conversationDepth = _history.Count(m => m.Role == ChatRole.User);
        var modelId = await modelRouter.ResolveModelAsync(userMessage, conversationDepth, cancellationToken);

        var chatOptions = new ChatOptions
        {
            Tools = _tools,
            ModelId = modelId
        };

        // Adaptive thinking is only supported on Sonnet and Opus, not Haiku
        if (modelId != AnthropicModels.Claude45Haiku)
        {
            chatOptions = chatOptions.WithAdaptiveThinking();
        }

        logger.LogDebug("Sending message to Claude ({Model}) with {ToolCount} tools available", modelId, _tools.Count);

        var updates = new List<ChatResponseUpdate>();
        var channel = Channel.CreateUnbounded<string>();

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await resiliencePipeline.ExecuteAsync(async ct =>
                {
                    // Track current thinking/response state
                    OutputItem? thinkingItem = null;
                    var thinkingBuilder = new StringBuilder();
                    DateTime? thinkingStartTime = null;

                    OutputItem? responseItem = null;
                    var responseBuilder = new StringBuilder();

                    await foreach (var update in chatClient.GetStreamingResponseAsync(_history, chatOptions, ct))
                    {
                        updates.Add(update);

                        foreach (var content in update.Contents)
                        {
                            if (content is TextReasoningContent reasoning)
                            {
                                // If we were building a response, close it (interleaved thinking)
                                if (responseItem is not null)
                                {
                                    responseItem.Status = OutputItemStatus.Success;
                                    responseItem.Delta = null;
                                    outputListener.OnStepCompleted(responseItem);
                                    responseItem = null;
                                    responseBuilder.Clear();
                                }

                                // Start a new thinking block if needed
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

                                thinkingBuilder.Append(reasoning.Text);
                                var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                                thinkingItem.Delta = reasoning.Text;
                                thinkingItem.Body = thinkingBuilder.ToString();
                                thinkingItem.Title = elapsed > 0 ? $"Thought for {elapsed}s" : "Thinking...";
                                outputListener.OnStepUpdated(thinkingItem);
                            }
                            else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                            {
                                // Close thinking block if we were thinking
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

                                responseBuilder.Append(textContent.Text);
                                responseItem.Delta = textContent.Text;
                                responseItem.Body = responseBuilder.ToString();
                                outputListener.OnStepUpdated(responseItem);

                                await channel.Writer.WriteAsync(textContent.Text, ct);
                            }
                        }
                    }

                    // Close any remaining thinking block
                    if (thinkingItem is not null)
                    {
                        var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                        thinkingItem.Status = OutputItemStatus.Success;
                        thinkingItem.Title = $"Thought for {elapsed}s";
                        thinkingItem.Delta = null;
                        outputListener.OnStepCompleted(thinkingItem);
                    }

                    // Close any remaining response block
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

        _history.AddMessages(updates.ToChatResponse());
    }

    public Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default)
        => modelRouter.GenerateTitleAsync(userMessage, cancellationToken);

    public void ClearHistory()
    {
        _history.Clear();
        logger.LogInformation("Conversation history cleared");
    }

    public string SerializeHistory()
    {
        var entries = _history.Select(m => new SerializedChatMessage
        {
            Role = m.Role.Value,
            Text = m.Text ?? "",
            Contents = m.Contents?.Select(c => c switch
            {
                TextContent tc => new SerializedContent { Type = "text", Text = tc.Text },
                TextReasoningContent trc => new SerializedContent { Type = "thinking", Text = trc.Text },
                _ => new SerializedContent { Type = "other", Text = c.ToString() ?? "" }
            }).ToList()
        }).ToList();

        return JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public void RestoreHistory(string serializedHistory)
    {
        var entries = JsonSerializer.Deserialize<List<SerializedChatMessage>>(serializedHistory, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (entries is null) return;

        _history.Clear();

        foreach (var entry in entries)
        {
            var role = new ChatRole(entry.Role);
            var contents = new List<AIContent>();

            if (entry.Contents is not null)
            {
                foreach (var c in entry.Contents)
                {
                    switch (c.Type)
                    {
                        case "thinking":
                            contents.Add(new TextReasoningContent(c.Text ?? ""));
                            break;
                        case "text":
                        default:
                            contents.Add(new TextContent(c.Text ?? ""));
                            break;
                    }
                }
            }

            if (contents.Count > 0)
                _history.Add(new ChatMessage(role, contents));
            else
                _history.Add(new ChatMessage(role, entry.Text));
        }

        logger.LogInformation("Restored conversation history with {Count} messages", _history.Count);
    }

    private class SerializedChatMessage
    {
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
        public List<SerializedContent>? Contents { get; set; }
    }

    private class SerializedContent
    {
        public string Type { get; set; } = "text";
        public string? Text { get; set; }
    }
}
