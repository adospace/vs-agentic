using System.Text.Json;
using System.Text.Json.Serialization;
using VsAgentic.Services.Abstractions;

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
    /// Build the JSON line for a user message carrying one or more images.
    /// The CLI accepts base64 image blocks inline, so the bytes go straight into
    /// the conversation without being written to the project first.
    ///
    /// Images come before the text: the API reads a prompt that refers to
    /// "this screenshot" more reliably when the image is already in context.
    /// </summary>
    public static string BuildUserMessageWithImages(
        string text,
        IReadOnlyList<ChatImageAttachment> images)
    {
        var content = new List<object>(images.Count + 1);

        foreach (var image in images)
        {
            content.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = image.MediaType,
                    data = Convert.ToBase64String(image.Data)
                }
            });
        }

        // An empty text block is rejected, so only add one when there is text.
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.Add(new { type = "text", text });
        }

        var msg = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = content.ToArray()
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
