using System.IO;
using System.Text;
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

    /// <summary>
    /// Build one control request line:
    /// <c>{"type":"control_request","request_id":…,"request":{"subtype":…, …}}</c>.
    /// <paramref name="writeFields"/> adds the request's own fields after the
    /// subtype.
    ///
    /// Written by hand rather than through <see cref="SerializerOptions"/>,
    /// because that drops null values, and for some requests the null is the
    /// message.
    /// </summary>
    public static string BuildControlRequest(
        string requestId, string subtype, Action<Utf8JsonWriter>? writeFields = null)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "control_request");
            w.WriteString("request_id", requestId);
            w.WriteStartObject("request");
            w.WriteString("subtype", subtype);
            writeFields?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Build the <c>initialize</c> request. It registers nothing: no hooks, no
    /// SDK MCP servers, no prompt changes. The point is the answer, which
    /// describes the process (see <see cref="CliProcessInfo"/>).
    /// </summary>
    public static string BuildInitialize(string requestId) =>
        BuildControlRequest(requestId, "initialize");

    /// <summary>
    /// Build the <c>interrupt</c> request, which aborts the running turn.
    /// <c>cancel_queued</c> also drops the messages still waiting in the CLI's
    /// queue, so Stop leaves nothing behind that would start on its own. A CLI
    /// that predates the field ignores it.
    /// </summary>
    public static string BuildInterrupt(string requestId) =>
        BuildControlRequest(requestId, "interrupt", w => w.WriteBoolean("cancel_queued", true));

    /// <summary>
    /// Build the control request that changes the model and/or effort of the
    /// running process. A setting is sent only when its <c>include</c> flag is
    /// set; a null value then returns it to the CLI default.
    /// </summary>
    public static string BuildApplyFlagSettings(
        string requestId,
        bool includeModel, string? model,
        bool includeEffort, string? effort) =>
        BuildControlRequest(requestId, "apply_flag_settings", w =>
        {
            w.WriteStartObject("settings");
            if (includeModel) WriteStringOrNull(w, "model", model);
            if (includeEffort) WriteStringOrNull(w, "effortLevel", effort);
            w.WriteEndObject();
        });

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }
}
