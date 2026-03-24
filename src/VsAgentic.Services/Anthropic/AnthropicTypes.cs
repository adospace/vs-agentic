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

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

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
}
