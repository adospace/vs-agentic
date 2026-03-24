using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.Anthropic;

/// <summary>
/// Core chat engine that handles streaming responses and the tool-call loop.
/// Replaces IChatClient + FunctionInvokingChatClient from Microsoft.Extensions.AI.
///
/// CRITICAL: Tool call arguments are assembled by concatenating raw input_json_delta
/// strings from the SSE stream. No intermediate parsing or re-serialization occurs.
/// This preserves exact whitespace in tool arguments (the bug in the SDK).
/// </summary>
public sealed class ChatEngine
{
    private readonly AnthropicHttpClient _client;
    private readonly ILogger _logger;

    public ChatEngine(AnthropicHttpClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Callbacks for streaming events. ChatService wires these to OutputListener.
    /// </summary>
    public sealed class Callbacks
    {
        public Action<string>? OnTextDelta { get; init; }
        public Action<string>? OnThinkingDelta { get; init; }
        public Action<string, string>? OnToolCallStarted { get; init; }   // (toolName, toolUseId)
        public Action<string, string>? OnToolCallCompleted { get; init; } // (toolName, result)
    }

    /// <summary>
    /// Maximum number of tool-call loop iterations before forcing a stop.
    /// Prevents runaway cost from unbounded tool chains.
    /// </summary>
    private const int MaxToolIterations = 25;

    /// <summary>
    /// Thinking budget for the first iteration (planning phase).
    /// </summary>
    private const int InitialThinkingBudget = 10000;

    /// <summary>
    /// Reduced thinking budget for subsequent tool-loop iterations
    /// (processing results, deciding next step — less reasoning needed).
    /// </summary>
    private const int FollowUpThinkingBudget = 4000;

    /// <summary>
    /// Runs a full conversation turn: streams the response, invokes tools if requested,
    /// loops until the model produces end_turn. Appends all messages to history.
    /// </summary>
    public async Task RunAsync(
        string modelId,
        string? systemPrompt,
        List<Message> history,
        IReadOnlyList<ToolDefinition> tools,
        bool enableThinking,
        Callbacks? callbacks,
        SessionTokenUsage sessionUsage,
        CancellationToken cancellationToken)
    {
        var toolMap = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var toolSpecs = tools.Select(t => new ToolSpec
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema
        }).ToList();

        // Set cache_control on the last tool so the API caches system prompt + all tools.
        // This dramatically reduces input token costs on subsequent calls in the tool loop.
        if (toolSpecs.Count > 0)
        {
            var last = toolSpecs[^1];
            toolSpecs[^1] = new ToolSpec
            {
                Name = last.Name,
                Description = last.Description,
                InputSchema = last.InputSchema,
                CacheControl = new CacheControl { Type = "ephemeral" }
            };
        }

        // Build system prompt blocks with cache_control for prompt caching
        var systemBlocks = MessagesRequest.BuildSystemBlocks(systemPrompt);

        // Tool loop: keep sending until model doesn't request more tools
        var iteration = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (iteration >= MaxToolIterations)
            {
                _logger.LogWarning("Tool loop reached max iterations ({Max}), forcing stop", MaxToolIterations);
                break;
            }

            var thinkingBudget = iteration == 0 ? InitialThinkingBudget : FollowUpThinkingBudget;

            var request = new MessagesRequest
            {
                Model = modelId,
                MaxTokens = enableThinking ? 16000 : 8192,
                System = systemBlocks,
                Messages = history,
                Tools = toolSpecs.Count > 0 ? toolSpecs : null,
                Stream = true,
                Thinking = enableThinking ? new ThinkingConfig { Type = "enabled", BudgetTokens = thinkingBudget } : null
            };

            var assistantContent = new List<ContentBlock>();

            var stream = await _client.StreamAsync(request, cancellationToken);
            string? stopReason;
            UsageInfo turnUsage;
            using (stream)
            {
                (stopReason, turnUsage) = await ProcessStreamAsync(stream, assistantContent, callbacks, cancellationToken);
            }

            // Track cumulative usage (pass modelId so cost can be calculated later)
            sessionUsage.Add(turnUsage, modelId);

            _logger.LogInformation(
                "[Tokens] iter={Iteration} model={Model} in={Input} out={Output} cache_create={CacheCreate} cache_read={CacheRead} | cumulative: in={TotalIn} out={TotalOut} calls={Calls}",
                iteration, modelId,
                turnUsage.InputTokens, turnUsage.OutputTokens,
                turnUsage.CacheCreationInputTokens, turnUsage.CacheReadInputTokens,
                sessionUsage.TotalInputTokens, sessionUsage.TotalOutputTokens, sessionUsage.ApiCalls);

            // Append the assistant message to history.
            // Redact thinking content to save tokens — the API requires thinking blocks
            // in history for signature verification, but the text itself is not needed.
            var historyContent = assistantContent.Select<ContentBlock, ContentBlock>(b => b switch
            {
                ThinkingBlock tb => new ThinkingBlock
                {
                    Thinking = "[redacted]",
                    Signature = tb.Signature
                },
                _ => b
            }).ToList();

            history.Add(new Message
            {
                Role = "assistant",
                Content = (object)historyContent
            });

            _logger.LogDebug("Assistant turn complete: {BlockCount} blocks, stop_reason={StopReason}, iteration={Iteration}",
                assistantContent.Count, stopReason, iteration);

            iteration++;

            // If no tool use requested, we're done
            if (stopReason != "tool_use") break;

            // Invoke tools and build tool results
            var toolResults = new List<ContentBlock>();
            foreach (var block in assistantContent.OfType<ToolUseBlock>())
            {
                callbacks?.OnToolCallStarted?.Invoke(block.Name, block.Id);

                string result;
                try
                {
                    if (toolMap.TryGetValue(block.Name, out var tool))
                    {
                        _logger.LogDebug("Invoking tool '{ToolName}' (id={ToolId})", block.Name, block.Id);
                        result = await tool.InvokeAsync(block.Input, cancellationToken);
                    }
                    else
                    {
                        result = $"[error]: Unknown tool '{block.Name}'";
                        _logger.LogWarning("Unknown tool requested: {ToolName}", block.Name);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = $"[error]: {ex.Message}";
                    _logger.LogWarning(ex, "Tool '{ToolName}' failed", block.Name);
                }

                callbacks?.OnToolCallCompleted?.Invoke(block.Name, result);

                toolResults.Add(new ToolResultBlock
                {
                    ToolUseId = block.Id,
                    Content = result
                });
            }

            // Append tool results as a user message
            history.Add(new Message
            {
                Role = "user",
                Content = (object)toolResults.ToList()
            });
        }
    }

    /// <summary>
    /// Processes the SSE stream, assembling content blocks.
    /// Returns the stop_reason and token usage from the stream events.
    ///
    /// CRITICAL: input_json_delta chunks are concatenated via StringBuilder.Append()
    /// with NO trimming, no re-parsing, no normalization. This preserves whitespace exactly.
    /// </summary>
    private async Task<(string? StopReason, UsageInfo Usage)> ProcessStreamAsync(
        Stream stream,
        List<ContentBlock> contentBlocks,
        Callbacks? callbacks,
        CancellationToken cancellationToken)
    {
        string? stopReason = null;
        var usage = new UsageInfo();

        // State for the current content block being assembled
        int currentIndex = -1;
        string? currentBlockType = null;
        string? currentToolId = null;
        string? currentToolName = null;
        var textBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        var signatureBuilder = new StringBuilder();
        var inputJsonBuilder = new StringBuilder(); // ← THE FIX: raw JSON accumulation

        await foreach (var evt in SseParser.ParseAsync(stream, cancellationToken))
        {
            switch (evt.EventType)
            {
                case "content_block_start":
                {
                    var index = evt.Data.GetProperty("index").GetInt32();
                    var block = evt.Data.GetProperty("content_block");
                    var type = block.GetProperty("type").GetString()!;

                    currentIndex = index;
                    currentBlockType = type;
                    textBuilder.Clear();
                    thinkingBuilder.Clear();
                    signatureBuilder.Clear();
                    inputJsonBuilder.Clear();

                    if (type == "tool_use")
                    {
                        currentToolId = block.GetProperty("id").GetString()!;
                        currentToolName = block.GetProperty("name").GetString()!;
                    }

                    break;
                }

                case "content_block_delta":
                {
                    var delta = evt.Data.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString()!;

                    switch (deltaType)
                    {
                        case "text_delta":
                        {
                            var text = delta.GetProperty("text").GetString()!;
                            textBuilder.Append(text);
                            callbacks?.OnTextDelta?.Invoke(text);
                            break;
                        }

                        case "thinking_delta":
                        {
                            var thinking = delta.GetProperty("thinking").GetString()!;
                            thinkingBuilder.Append(thinking);
                            callbacks?.OnThinkingDelta?.Invoke(thinking);
                            break;
                        }

                        case "signature_delta":
                        {
                            var sig = delta.GetProperty("signature").GetString()!;
                            signatureBuilder.Append(sig);
                            break;
                        }

                        case "input_json_delta":
                        {
                            // ═══════════════════════════════════════════════════════════
                            // THIS IS THE CRITICAL FIX.
                            // Append the raw partial_json string EXACTLY as received.
                            // No trimming. No parsing. No re-serialization.
                            // Every space, every character is preserved.
                            // ═══════════════════════════════════════════════════════════
                            var partialJson = delta.GetProperty("partial_json").GetString()!;
                            inputJsonBuilder.Append(partialJson);
                            break;
                        }
                    }

                    break;
                }

                case "content_block_stop":
                {
                    // Finalize the current block
                    switch (currentBlockType)
                    {
                        case "text":
                            contentBlocks.Add(new TextBlock { Text = textBuilder.ToString() });
                            break;

                        case "thinking":
                            contentBlocks.Add(new ThinkingBlock
                            {
                                Thinking = thinkingBuilder.ToString(),
                                Signature = signatureBuilder.ToString()
                            });
                            break;

                        case "tool_use":
                        {
                            // Parse the assembled JSON string ONCE into a JsonElement
                            var rawJson = inputJsonBuilder.ToString();
                            _logger.LogTrace("[ChatEngine] Tool '{ToolName}' raw input JSON ({Len} chars):\n{Json}",
                                currentToolName, rawJson.Length, rawJson);

                            JsonElement input;
                            try
                            {
                                using var doc = JsonDocument.Parse(rawJson);
                                input = doc.RootElement.Clone();
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogWarning(ex, "Failed to parse tool input JSON for '{ToolName}'", currentToolName);
                                input = JsonDocument.Parse("{}").RootElement.Clone();
                            }

                            contentBlocks.Add(new ToolUseBlock
                            {
                                Id = currentToolId!,
                                Name = currentToolName!,
                                Input = input
                            });
                            break;
                        }
                    }

                    currentIndex = -1;
                    currentBlockType = null;
                    currentToolId = null;
                    currentToolName = null;
                    break;
                }

                case "message_start":
                {
                    // message_start contains initial usage (input tokens, cache info)
                    if (evt.Data.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("usage", out var startUsage))
                    {
                        if (startUsage.TryGetProperty("input_tokens", out var it))
                            usage.InputTokens = it.GetInt32();
                        if (startUsage.TryGetProperty("cache_creation_input_tokens", out var cc))
                            usage.CacheCreationInputTokens = cc.GetInt32();
                        if (startUsage.TryGetProperty("cache_read_input_tokens", out var cr))
                            usage.CacheReadInputTokens = cr.GetInt32();
                    }
                    break;
                }

                case "message_delta":
                {
                    if (evt.Data.TryGetProperty("delta", out var msgDelta) &&
                        msgDelta.TryGetProperty("stop_reason", out var sr))
                    {
                        stopReason = sr.GetString();
                    }
                    // message_delta contains final usage (output tokens)
                    if (evt.Data.TryGetProperty("usage", out var deltaUsage))
                    {
                        if (deltaUsage.TryGetProperty("output_tokens", out var ot))
                            usage.OutputTokens = ot.GetInt32();
                    }
                    break;
                }
            }
        }

        return (stopReason, usage);
    }
}
