using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Strongly-typed records for the Claude CLI's bidirectional stream-json protocol
/// (<c>--input-format stream-json --output-format stream-json --verbose</c>).
///
/// We model only the subset we read or write. Anything else flows through as
/// <see cref="JsonElement"/> and is left to ad-hoc inspection.
/// </summary>
internal static class StreamJsonProtocol
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Build the JSON line for a plain-text user message sent to the CLI over stdin.
    /// </summary>
    public static string BuildUserTextMessage(string text)
    {
        var msg = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = text
            }
        };
        return JsonSerializer.Serialize(msg, SerializerOptions);
    }

    /// <summary>
    /// Build the JSON line for a user message that satisfies a tool call (e.g.
    /// answers to an <c>AskUserQuestion</c>) by attaching a <c>tool_result</c> block.
    /// </summary>
    public static string BuildToolResultMessage(string toolUseId, string content)
    {
        var msg = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "tool_result",
                        tool_use_id = toolUseId,
                        content
                    }
                }
            }
        };
        return JsonSerializer.Serialize(msg, SerializerOptions);
    }
}
