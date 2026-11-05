using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.ClaudeCli.Questions;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Hosts a named pipe inside the parent extension process. The MCP permission
/// helper exe (spawned by the Claude CLI as a stdio MCP server) connects back
/// to this pipe to forward permission requests it receives from the CLI.
///
/// Wire format: newline-delimited JSON. Every request carries an id, and its
/// response carries the same id, so requests can overlap: a slow IDE tool
/// does not hold up a permission prompt behind it.
///   permission   client → server  {"id":"<guid>","tool":"Bash","input":{...},"toolUseId":"toolu_…"}
///                server → client  {"id":"<guid>","behavior":"allow","updatedInput":{...}}
///                                 {"id":"<guid>","behavior":"deny","message":"..."}
///   tools/list   client → server  {"id":"<guid>","type":"tools/list"}
///                server → client  {"id":"<guid>","tools":[...]}
///   tools/call   client → server  {"id":"<guid>","type":"tools/call","name":"get_diagnostics","arguments":{...}}
///                server → client  {"id":"<guid>","text":"...","isError":false}
///   withdraw     client → server  {"id":"<guid>","cancel":true}   (the CLI cancelled the call; no reply is read)
///
/// One pipe instance per CLI process. Started by <c>ClaudeCliProcessHost</c>
/// before launching the CLI; the pipe name + shared secret are passed to the
/// MCP helper via env vars in the --mcp-config block.
/// </summary>
internal sealed class PermissionPipeServer : IDisposable
{
    private readonly IPermissionBroker _broker;
    private readonly IUserQuestionBroker _questionBroker;
    private readonly IdeContextRegistry? _ideContext;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task? _acceptLoop;

    public string PipeName => _pipeName;
    public string Secret => _secret;

    public PermissionPipeServer(IPermissionBroker broker, IUserQuestionBroker questionBroker, IdeContextRegistry? ideContext, ILogger logger)
    {
        _broker = broker;
        _questionBroker = questionBroker;
        _ideContext = ideContext;
        _logger = logger;
        _pipeName = $"vsagentic-perm-{Guid.NewGuid():N}";
        _secret = Guid.NewGuid().ToString("N");
    }

    public void Start()
    {
        _logger.LogInformation("[PermissionPipeServer] listening on pipe '{Name}'", _pipeName);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await Task.Factory.FromAsync(
                    server.BeginWaitForConnection,
                    server.EndWaitForConnection,
                    state: null).ConfigureAwait(false);

                _logger.LogInformation("[PermissionPipeServer] client connected on '{Name}'", _pipeName);

                // Hand off to a per-connection handler so we can accept the next.
                var handlerStream = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(handlerStream, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PermissionPipeServer] accept loop error on pipe '{Name}'", _pipeName);
                server?.Dispose();
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
            {
                // Handshake: first line must be the secret.
                var hello = await reader.ReadLineAsync().ConfigureAwait(false);
                if (hello != _secret)
                {
                    _logger.LogWarning("[PermissionPipeServer] handshake failed (got '{Hello}')", hello);
                    return;
                }
                _logger.LogInformation("[PermissionPipeServer] handshake ok");

                // Requests are answered as they finish, not in order, so the
                // writes need a lock of their own.
                var writeLock = new SemaphoreSlim(1, 1);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var (id, kind) = PeekRequest(line);
                    if (kind == "cancel")
                    {
                        // The CLI cancelled the call. Its answer is no longer
                        // read, so this only takes the prompt off the screen.
                        // Ids are unique, and each broker ignores ones it
                        // does not hold.
                        _logger.LogInformation("[PermissionPipeServer] request {Id} withdrawn by the CLI", id);
                        _broker.Withdraw(id);
                        _questionBroker.Withdraw(id);
                        continue;
                    }

                    // A permission request is handed to the broker here rather
                    // than on the task below: the broker registers it before
                    // this returns, so a withdrawal on the next line always
                    // finds it. IDE tools may do real work, so they start on
                    // a task of their own.
                    var responseTask = kind == "permission"
                        ? HandleRequestAsync(line, ct)
                        : Task.Run(() => HandleRequestAsync(line, ct));

                    _ = Task.Run(async () =>
                    {
                        // Every request whose id can be read gets an answer:
                        // the helper waits for one with no time limit, and so
                        // does the CLI's tool call behind it.
                        string? response;
                        try
                        {
                            response = await responseTask.ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[PermissionPipeServer] request failed: {Line}", line);
                            response = TryReadHeader(line, out var failedId, out var failedType)
                                ? ErrorResponse(failedId, failedType, "Internal error in Visual Studio")
                                : null;
                        }
                        if (response is null) return;

                        try
                        {
                            await writeLock.WaitAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return; // The connection is closing; the helper fails its pending calls.
                        }
                        try
                        {
                            await writer.WriteLineAsync(response).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[PermissionPipeServer] could not write the response");
                        }
                        finally
                        {
                            writeLock.Release();
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PermissionPipeServer] connection handler crashed");
        }
    }

    /// <summary>The request's id and kind: "cancel", "permission", or its type.</summary>
    private static (string Id, string Kind) PeekRequest(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (root.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True)
                return (id, "cancel");
            return (id, root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "permission");
        }
        catch (JsonException)
        {
            // HandleRequestAsync logs the malformed line.
            return ("", "");
        }
    }

    /// <summary>The response line for one request, or null when not even its id can be read.</summary>
    private async Task<string?> HandleRequestAsync(string line, CancellationToken ct)
    {
        if (!TryReadHeader(line, out var id, out var type))
        {
            _logger.LogWarning("[PermissionPipeServer] request without a readable id: {Line}", line);
            return null;
        }

        string toolName = "";
        string? toolUseId = null;
        JsonElement input = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Clone so we can use them after the doc is disposed.
            if (type == "tools/call")
            {
                toolName = root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                if (root.TryGetProperty("arguments", out var args))
                    input = args.Clone();
            }
            else if (type == "permission")
            {
                toolName = root.GetProperty("tool").GetString() ?? "";
                if (root.TryGetProperty("input", out var inp))
                    input = inp.Clone();
                if (root.TryGetProperty("toolUseId", out var tu) && tu.ValueKind == JsonValueKind.String)
                    toolUseId = tu.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PermissionPipeServer] malformed request: {Line}", line);
            return ErrorResponse(id, type, "Malformed request");
        }

        switch (type)
        {
            case "tools/list":
                return "{\"id\":" + JsonSerializer.Serialize(id) + ",\"tools\":" + (_ideContext?.BuildToolsJson() ?? "[]") + "}";

            case "tools/call":
                return await HandleIdeToolCallAsync(id, toolName, input, ct).ConfigureAwait(false);

            case "permission":
                var decision = await DecidePermissionAsync(id, toolName, input, toolUseId, ct).ConfigureAwait(false);
                return SerializeDecision(id, decision);

            default:
                _logger.LogWarning("[PermissionPipeServer] unknown request type '{Type}'", type);
                return ErrorResponse(id, type, $"Unknown request type '{type}'");
        }
    }

    /// <summary>The id and type of a request, as far as the line can be read.
    /// A request without a type is a permission request.</summary>
    private static bool TryReadHeader(string line, out string id, out string type)
    {
        id = "";
        type = "permission";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                return false;
            id = idEl.GetString() ?? "";
            if (root.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String)
                type = tp.GetString() ?? "permission";
            return id.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>An answer that ends the request with an error, in the shape
    /// the helper expects for its type. A permission request, or one of an
    /// unknown type, is denied.</summary>
    private static string ErrorResponse(string id, string type, string message) => type switch
    {
        "tools/list" => "{\"id\":" + JsonSerializer.Serialize(id) + ",\"tools\":[]}",
        "tools/call" => "{\"id\":" + JsonSerializer.Serialize(id) +
                        ",\"text\":" + JsonSerializer.Serialize(message) + ",\"isError\":true}",
        _ => SerializeDecision(id, PermissionDecision.Deny(message)),
    };

    private async Task<PermissionDecision> DecidePermissionAsync(string id, string toolName, JsonElement input, string? toolUseId, CancellationToken ct)
    {
        // AskUserQuestion is the documented Anthropic flow for clarifying
        // questions: the host gathers answers and returns them via the
        // permission "allow" decision's updatedInput { questions, answers }.
        // The CLI then runs the tool with that injected input and the model
        // sees the answers in the resulting tool_result. Returning a plain
        // "allow" with the raw input — or trying to write our own tool_result
        // afterwards — leaves the model with empty answers.
        if (toolName == "AskUserQuestion")
            return await HandleAskUserQuestionAsync(id, input, ct).ConfigureAwait(false);

        // An IDE tool the user turned off is refused without a banner: a banner
        // would ask to allow what the setting does not allow. Refused rather
        // than allowed, so turning access on in between cannot let the call
        // run without the banner.
        if (toolName.StartsWith(IdeContextRegistry.McpToolPrefix, StringComparison.Ordinal) &&
            _ideContext?.IsDisabled(toolName) == true)
        {
            _logger.LogInformation("[PermissionPipeServer] refused turned-off IDE tool {Tool}", toolName);
            return PermissionDecision.Deny(IdeContextRegistry.TurnedOffNote);
        }

        // Our own read-only IDE tools need no banner (see IdeTool.IsReadOnly).
        if (toolName.StartsWith(IdeContextRegistry.McpToolPrefix, StringComparison.Ordinal) &&
            _ideContext?.Find(toolName) is { IsReadOnly: true })
        {
            _logger.LogInformation("[PermissionPipeServer] allowed read-only IDE tool {Tool}", toolName);
            return PermissionDecision.Allow(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText());
        }

        var request = new PermissionRequest(id, toolName, input, toolUseId);
        try
        {
            return await _broker.SubmitAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PermissionPipeServer] broker.SubmitAsync threw");
            return PermissionDecision.Deny("Internal error");
        }
    }

    private async Task<string> HandleIdeToolCallAsync(string id, string toolName, JsonElement arguments, CancellationToken ct)
    {
        var result = _ideContext is null
            ? IdeToolResult.Error("No IDE tools are available.")
            : await _ideContext.InvokeAsync(toolName, arguments, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[PermissionPipeServer] IDE tool {Tool} -> {Length} chars{Error}",
            toolName, result.Text.Length, result.IsError ? " (error)" : "");

        return "{\"id\":" + JsonSerializer.Serialize(id) +
               ",\"text\":" + JsonSerializer.Serialize(result.Text) +
               ",\"isError\":" + (result.IsError ? "true" : "false") + "}";
    }

    private async Task<PermissionDecision> HandleAskUserQuestionAsync(
        string id, JsonElement input, CancellationToken ct)
    {
        var questions = ParseQuestions(input);
        var request = new UserQuestionRequest(id, questions, input);

        IReadOnlyDictionary<string, string> answers;
        try
        {
            answers = await _questionBroker.SubmitAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PermissionPipeServer] question broker threw");
            answers = new Dictionary<string, string>();
        }

        var updatedInputJson = BuildUpdatedInputJson(input, answers);
        _logger.LogInformation(
            "[PermissionPipeServer] AskUserQuestion answered (id={Id}) -> {Json}",
            id, updatedInputJson);
        return PermissionDecision.Allow(updatedInputJson);
    }

    private static IReadOnlyList<UserQuestion> ParseQuestions(JsonElement input)
    {
        var list = new List<UserQuestion>();
        if (input.ValueKind != JsonValueKind.Object) return list;
        if (!input.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var q in qs.EnumerateArray())
        {
            var uq = new UserQuestion
            {
                Question = q.TryGetProperty("question", out var qt) ? qt.GetString() ?? "" : "",
                Header = q.TryGetProperty("header", out var hd) ? hd.GetString() ?? "" : "",
                MultiSelect = q.TryGetProperty("multiSelect", out var ms) && ms.ValueKind == JsonValueKind.True,
            };
            if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    uq.Options.Add(new UserQuestionOption
                    {
                        Label = o.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "",
                        Description = o.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "",
                    });
                }
            }
            list.Add(uq);
        }
        return list;
    }

    /// <summary>
    /// Build the <c>updatedInput</c> shape Anthropic's docs prescribe for the
    /// AskUserQuestion permission "allow" reply: original questions array passed
    /// through verbatim, plus an answers map keyed by each question's text and
    /// valued with the chosen option's label (or comma-joined labels for
    /// multi-select, or the user's free-text answer).
    /// </summary>
    private static string BuildUpdatedInputJson(
        JsonElement input,
        IReadOnlyDictionary<string, string> answers)
    {
        var sb = new StringBuilder();
        sb.Append("{\"questions\":");
        if (input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty("questions", out var qs))
            sb.Append(qs.GetRawText());
        else
            sb.Append("[]");
        sb.Append(",\"answers\":");
        sb.Append(JsonSerializer.Serialize(answers));
        sb.Append("}");
        return sb.ToString();
    }

    private static string SerializeDecision(string id, PermissionDecision decision)
    {
        if (decision.Behavior == PermissionBehavior.Allow)
        {
            // updatedInput is raw JSON; embed it directly so the helper can pass
            // it back to the CLI without re-parsing.
            var sb = new StringBuilder();
            sb.Append("{\"id\":");
            sb.Append(JsonSerializer.Serialize(id));
            sb.Append(",\"behavior\":\"allow\",\"updatedInput\":");
            sb.Append(decision.UpdatedInputJson ?? "{}");
            AppendMode(sb, decision);
            sb.Append("}");
            return sb.ToString();
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":");
            sb.Append(JsonSerializer.Serialize(id));
            sb.Append(",\"behavior\":\"deny\",\"message\":");
            sb.Append(JsonSerializer.Serialize(decision.Message ?? "User denied"));
            sb.Append("}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Appends the mode switch the CLI reads off an allow response. With
    /// destination <c>session</c> it changes the running session only and
    /// writes nothing to disk. Measured on CLI 2.1.316 for <c>ExitPlanMode</c>:
    /// the CLI switches before it runs the call and reports the new mode in
    /// <c>system/status</c>, so the mode picker follows. The helper process
    /// forwards every field of this object except <c>id</c>.
    /// </summary>
    private static void AppendMode(StringBuilder sb, PermissionDecision decision)
    {
        if (decision.Mode is null) return;

        sb.Append(",\"updatedPermissions\":[{\"type\":\"setMode\",\"mode\":");
        sb.Append(JsonSerializer.Serialize(decision.Mode));
        sb.Append(",\"destination\":\"session\"}]");
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
