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
///   permission   client → server  {"id":"<guid>","tool":"Bash","input":{...}}
///                server → client  {"id":"<guid>","behavior":"allow","updatedInput":{...}}
///                                 {"id":"<guid>","behavior":"deny","message":"..."}
///   tools/list   client → server  {"id":"<guid>","type":"tools/list"}
///                server → client  {"id":"<guid>","tools":[...]}
///   tools/call   client → server  {"id":"<guid>","type":"tools/call","name":"get_diagnostics","arguments":{...}}
///                server → client  {"id":"<guid>","text":"...","isError":false}
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

                    _ = Task.Run(async () =>
                    {
                        var response = await HandleRequestAsync(line, ct).ConfigureAwait(false);
                        if (response is null) return;

                        await writeLock.WaitAsync(ct).ConfigureAwait(false);
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

    /// <summary>The response line for one request, or null for a request that cannot be answered.</summary>
    private async Task<string?> HandleRequestAsync(string line, CancellationToken ct)
    {
        string id;
        string type;
        string toolName = "";
        JsonElement input = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString() ?? "";
            type = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "permission";

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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PermissionPipeServer] malformed request: {Line}", line);
            return null;
        }

        switch (type)
        {
            case "tools/list":
                return "{\"id\":" + JsonSerializer.Serialize(id) + ",\"tools\":" + (_ideContext?.BuildToolsJson() ?? "[]") + "}";

            case "tools/call":
                return await HandleIdeToolCallAsync(id, toolName, input, ct).ConfigureAwait(false);

            case "permission":
                var decision = await DecidePermissionAsync(id, toolName, input, ct).ConfigureAwait(false);
                return SerializeDecision(id, decision);

            default:
                _logger.LogWarning("[PermissionPipeServer] unknown request type '{Type}'", type);
                return null;
        }
    }

    private async Task<PermissionDecision> DecidePermissionAsync(string id, string toolName, JsonElement input, CancellationToken ct)
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

        // Our own read-only IDE tools need no banner (see IdeTool.IsReadOnly).
        if (toolName.StartsWith(IdeContextRegistry.McpToolPrefix, StringComparison.Ordinal) &&
            _ideContext?.Find(toolName) is { IsReadOnly: true })
        {
            _logger.LogInformation("[PermissionPipeServer] allowed read-only IDE tool {Tool}", toolName);
            return PermissionDecision.Allow(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText());
        }

        var request = new PermissionRequest(id, toolName, input);
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

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
