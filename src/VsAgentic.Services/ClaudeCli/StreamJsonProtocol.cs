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
    ///
    /// <paramref name="uuid"/> names the message. The CLI repeats it in the
    /// <c>command_lifecycle</c> events for the message and in the
    /// <c>user_message_uuids</c> of the turn's result, which is how a result is
    /// matched to the message that started the turn. A CLI that predates the
    /// field ignores it.
    /// </summary>
    public static string BuildUserTextMessage(string text, string uuid)
    {
        var msg = new
        {
            type = "user",
            uuid,
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
    /// <paramref name="uuid"/> is as in <see cref="BuildUserTextMessage"/>.
    /// </summary>
    public static string BuildUserMessageWithImages(
        string text,
        IReadOnlyList<ChatImageAttachment> images,
        string uuid)
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
            uuid,
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
    /// Build the control request that asks for the plan rate limits.
    /// <c>skip_behaviors</c> skips a scan of the last seven days of local
    /// transcripts that only the /usage dialog needs.
    /// </summary>
    public static string BuildGetUsage(string requestId) =>
        BuildControlRequest(requestId, "get_usage", w => w.WriteBoolean("skip_behaviors", true));

    /// <summary>
    /// Build the <c>initialize</c> request. It registers nothing: no hooks, no
    /// SDK MCP servers, no prompt changes. The point is the answer, which
    /// describes the process (see <see cref="CliProcessInfo"/>).
    ///
    /// <c>forwardSubagentText</c> asks for the text and thinking of subagents
    /// as well; without it the CLI sends only their tool calls, and nothing
    /// at all from a subagent that a subagent started. A CLI that predates the
    /// field ignores it, which a command-line flag it does not know would not.
    ///
    /// <c>perTaskStopAffordance</c> tells the CLI that the user can stop each
    /// background task on its own (the Stop button on the background tasks
    /// strip). Without it, an interrupt also kills the background subagents,
    /// because the CLI assumes nothing else could stop them.
    /// </summary>
    public static string BuildInitialize(string requestId) =>
        BuildControlRequest(requestId, "initialize", w =>
        {
            w.WriteBoolean("forwardSubagentText", true);
            w.WriteBoolean("perTaskStopAffordance", true);
        });

    /// <summary>
    /// Build the <c>stop_task</c> request, which stops one background task.
    /// The CLI then sends the task's <c>task_notification</c> with status
    /// "stopped".
    /// </summary>
    public static string BuildStopTask(string requestId, string taskId) =>
        BuildControlRequest(requestId, "stop_task", w => w.WriteString("task_id", taskId));

    /// <summary>
    /// Build the <c>background_tasks</c> request for one tool call: the
    /// command or subagent that tool call is running in the foreground moves
    /// to the background, and the turn goes on. Ctrl+B in the terminal.
    /// </summary>
    public static string BuildBackgroundTask(string requestId, string toolUseId) =>
        BuildControlRequest(requestId, "background_tasks", w => w.WriteString("tool_use_id", toolUseId));

    /// <summary>
    /// Build the <c>get_task_output</c> request: the last 8 KiB a shell task
    /// has written so far.
    /// </summary>
    public static string BuildGetTaskOutput(string requestId, string taskId) =>
        BuildControlRequest(requestId, "get_task_output", w => w.WriteString("task_id", taskId));

    /// <summary>
    /// Build the <c>interrupt</c> request, which aborts the running turn.
    /// <c>cancel_queued</c> also drops the messages still waiting in the CLI's
    /// queue, so Stop leaves nothing behind that would start on its own. A CLI
    /// that predates the field ignores it.
    /// </summary>
    public static string BuildInterrupt(string requestId) =>
        BuildControlRequest(requestId, "interrupt", w => w.WriteBoolean("cancel_queued", true));

    /// <summary>
    /// Build the <c>set_model</c> request. A null model returns the session to
    /// the account's default model, not to the <c>--model</c> the process was
    /// started with (measured on CLI 2.1.296).
    /// </summary>
    public static string BuildSetModel(string requestId, string? model) =>
        BuildControlRequest(requestId, "set_model", w => WriteStringOrNull(w, "model", model));

    /// <summary>
    /// Build the <c>get_settings</c> request. Its answer carries, under
    /// <c>applied</c>, the model and effort the next request will use.
    /// </summary>
    public static string BuildGetSettings(string requestId) =>
        BuildControlRequest(requestId, "get_settings");

    /// <summary>
    /// Build the <c>get_context_usage</c> request. Its answer carries
    /// <c>totalTokens</c>, what the conversation now takes of the context
    /// window: what <c>/context</c> shows in the terminal.
    /// </summary>
    public static string BuildGetContextUsage(string requestId) =>
        BuildControlRequest(requestId, "get_context_usage");

    /// <summary>
    /// Build the control request that changes the model and/or effort of the
    /// running process. A setting is sent only when its <c>include</c> flag is
    /// set; a null value then returns it to the CLI default.
    ///
    /// The model goes through <see cref="BuildSetModel"/>; this carries it only
    /// for a CLI that does not know that request.
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

    /// <summary>
    /// Build the control request that switches the permission mode of the
    /// running process. The CLI answers with the mode now in effect and also
    /// emits <c>system/status</c> with it.
    /// </summary>
    public static string BuildSetPermissionMode(string requestId, string mode) =>
        BuildControlRequest(requestId, "set_permission_mode", w => w.WriteString("mode", mode));

    /// <summary>
    /// Build the request that turns fast mode on or off:
    /// <c>apply_flag_settings {fastMode}</c>. There is no request of its own
    /// for it; the SDK's flag-settings example uses this key.
    /// </summary>
    public static string BuildFastMode(string requestId, bool on) =>
        BuildControlRequest(requestId, "apply_flag_settings", w =>
        {
            w.WriteStartObject("settings");
            w.WriteBoolean("fastMode", on);
            w.WriteEndObject();
        });

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }
}
