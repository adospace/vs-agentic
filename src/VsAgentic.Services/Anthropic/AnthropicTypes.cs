using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsAgentic.Services.Anthropic;

// ── Request types ──────────────────────────────────────────────────────────────

public sealed class MessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    /// <summary>
    /// System prompt as an array of content blocks (supports cache_control).
    /// Use <see cref="BuildSystemBlocks"/> to convert a plain string.
    /// </summary>
    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SystemBlock>? System { get; init; }

    [JsonPropertyName("messages")]
    public required List<Message> Messages { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolSpec>? Tools { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingConfig? Thinking { get; init; }

    /// <summary>
    /// Creates a system block array with cache_control for prompt caching.
    /// The system prompt is marked as a cache breakpoint. A second breakpoint on the
    /// last tool definition (set in ChatEngine) ensures tools are also cached.
    /// Both breakpoints together create an incremental cache: system → system+tools.
    /// </summary>
    public static List<SystemBlock>? BuildSystemBlocks(string? systemPrompt)
    {
        if (string.IsNullOrEmpty(systemPrompt)) return null;
        return
        [
            new SystemBlock
            {
                Type = "text",
                Text = systemPrompt!,
                CacheControl = new CacheControl { Type = "ephemeral" }
            }
        ];
    }
}

public sealed class SystemBlock
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheControl? CacheControl { get; init; }
}

public sealed class CacheControl
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

public sealed class ThinkingConfig
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "enabled";

    [JsonPropertyName("budget_tokens")]
    public int BudgetTokens { get; init; } = 10000;
}

public sealed class ToolSpec
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("input_schema")]
    public required JsonElement InputSchema { get; init; }

    /// <summary>
    /// Set on the LAST tool in the list to create a cache breakpoint
    /// covering system prompt + all tool definitions.
    /// </summary>
    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheControl? CacheControl { get; init; }
}

// ── Message types ──────────────────────────────────────────────────────────────

public sealed class Message
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    [JsonConverter(typeof(MessageContentConverter))]
    public required object Content { get; init; } // string or List<ContentBlock>
}

/// <summary>
/// Serializes Message.Content as either a JSON string (if it's a string)
/// or as an array of content blocks (if it's a list).
/// Handles polymorphic ContentBlock serialization.
/// </summary>
public sealed class MessageContentConverter : JsonConverter<object>
{
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString()!;

        // Array of content blocks — read as raw JSON elements
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case string s:
                writer.WriteStringValue(s);
                break;

            case System.Collections.IList list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    if (item is ContentBlock block)
                        WriteContentBlock(writer, block);
                    else
                        JsonSerializer.Serialize(writer, item, item.GetType(), options);
                }
                writer.WriteEndArray();
                break;

            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
    }

    private static void WriteContentBlock(Utf8JsonWriter writer, ContentBlock block)
    {
        writer.WriteStartObject();
        writer.WriteString("type", block.Type);

        switch (block)
        {
            case TextBlock tb:
                writer.WriteString("text", tb.Text);
                break;
            case ThinkingBlock thb:
                writer.WriteString("thinking", thb.Thinking);
                writer.WriteString("signature", thb.Signature);
                break;
            case ToolUseBlock tub:
                writer.WriteString("id", tub.Id);
                writer.WriteString("name", tub.Name);
                writer.WritePropertyName("input");
                tub.Input.WriteTo(writer);
                break;
            case ToolResultBlock trb:
                writer.WriteString("tool_use_id", trb.ToolUseId);
                writer.WriteString("content", trb.Content);
                break;
        }

        writer.WriteEndObject();
    }
}

// ── Content block types ────────────────────────────────────────────────────────

[JsonDerivedType(typeof(TextBlock))]
[JsonDerivedType(typeof(ThinkingBlock))]
[JsonDerivedType(typeof(ToolUseBlock))]
[JsonDerivedType(typeof(ToolResultBlock))]
public abstract class ContentBlock
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class TextBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

public sealed class ThinkingBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "thinking";

    [JsonPropertyName("thinking")]
    public required string Thinking { get; set; }

    [JsonPropertyName("signature")]
    public required string Signature { get; set; }
}

public sealed class ToolUseBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "tool_use";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }
}

public sealed class ToolResultBlock : ContentBlock
{
    [JsonPropertyName("type")]
    public override string Type => "tool_result";

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

// ── SSE streaming event data ───────────────────────────────────────────────────

public sealed class SseEvent
{
    public required string EventType { get; init; }
    public required JsonElement Data { get; init; }
}

// ── Non-streaming response ─────────────────────────────────────────────────────

public sealed class MessagesResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public List<JsonElement> Content { get; set; } = [];

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }
}

public sealed class UsageInfo
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; set; }
}

/// <summary>
/// Tracks cumulative token usage across a session for cost monitoring.
/// </summary>
public sealed class SessionTokenUsage
{
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public int TotalCacheCreationTokens { get; private set; }
    public int TotalCacheReadTokens { get; private set; }
    public int ApiCalls { get; private set; }

    /// <summary>The most recent model ID used in this session.</summary>
    public string? LastModelId { get; private set; }

    public void Add(UsageInfo usage, string? modelId = null)
    {
        TotalInputTokens += usage.InputTokens;
        TotalOutputTokens += usage.OutputTokens;
        TotalCacheCreationTokens += usage.CacheCreationInputTokens;
        TotalCacheReadTokens += usage.CacheReadInputTokens;
        ApiCalls++;
        if (modelId is not null)
            LastModelId = modelId;
    }

    /// <summary>
    /// Calculates the cumulative USD cost for this session using <see cref="LastModelId"/>.
    /// Returns null when no model has been recorded yet.
    /// </summary>
    public decimal? CalculateCost()
    {
        if (LastModelId is null) return null;
        return ModelPricing.CalculateCost(
            LastModelId,
            TotalInputTokens,
            TotalOutputTokens,
            TotalCacheCreationTokens,
            TotalCacheReadTokens);
    }

    public void Reset()
    {
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        TotalCacheCreationTokens = 0;
        TotalCacheReadTokens = 0;
        ApiCalls = 0;
        LastModelId = null;
    }

    public SessionTokenUsageSnapshot ToSnapshot() => new()
    {
        TotalInputTokens         = TotalInputTokens,
        TotalOutputTokens        = TotalOutputTokens,
        TotalCacheCreationTokens = TotalCacheCreationTokens,
        TotalCacheReadTokens     = TotalCacheReadTokens,
        LastModelId              = LastModelId
    };

    public void Restore(SessionTokenUsageSnapshot snapshot)
    {
        TotalInputTokens         = snapshot.TotalInputTokens;
        TotalOutputTokens        = snapshot.TotalOutputTokens;
        TotalCacheCreationTokens = snapshot.TotalCacheCreationTokens;
        TotalCacheReadTokens     = snapshot.TotalCacheReadTokens;
        LastModelId              = snapshot.LastModelId;
    }
}

/// <summary>
/// Plain data bag used to persist and restore token usage across IDE restarts.
/// </summary>
public sealed class SessionTokenUsageSnapshot
{
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public int TotalCacheCreationTokens { get; init; }
    public int TotalCacheReadTokens { get; init; }
    public string? LastModelId { get; init; }
}
