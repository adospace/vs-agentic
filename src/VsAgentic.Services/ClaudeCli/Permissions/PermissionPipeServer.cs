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

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Hosts a named pipe inside the parent extension process. The MCP permission
/// helper exe (spawned by the Claude CLI as a stdio MCP server) connects back
/// to this pipe to forward permission requests it receives from the CLI.
///
/// Wire format: newline-delimited JSON.
///   client → server  {"id":"<guid>","tool":"Bash","input":{...},"toolUseId":"toolu_…"}
///                    {"id":"<guid>","cancel":true}   (the CLI withdrew the request)
///   server → client  {"id":"<guid>","behavior":"allow","updatedInput":{...}}
///                    {"id":"<guid>","behavior":"deny","message":"..."}
/// Several requests can be open at once; replies come in any order.
///
/// One pipe instance per CLI process. Started by <c>ClaudeCliProcessHost</c>
/// before launching the CLI; the pipe name + shared secret are passed to the
/// MCP helper via env vars in the --mcp-config block.
/// </summary>
internal sealed class PermissionPipeServer : IDisposable
{
    private readonly IPermissionBroker _broker;
    private readonly IUserQuestionBroker _questionBroker;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task? _acceptLoop;

    public string PipeName => _pipeName;
    public string Secret => _secret;

    public PermissionPipeServer(IPermissionBroker broker, IUserQuestionBroker questionBroker, ILogger logger)
    {
        _broker = broker;
        _questionBroker = questionBroker;
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

                // Requests are answered on their own tasks: subagents running in
                // parallel ask at the same time, and one unanswered prompt must
                // not hold back the next one or a withdrawal of itself.
                var writeLock = new SemaphoreSlim(1, 1);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string id = "";
                    string toolName = "";
                    string? toolUseId = null;
                    bool withdraw = false;
                    JsonElement input = default;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        id = root.GetProperty("id").GetString() ?? "";
                        withdraw = root.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.True;
                        if (!withdraw)
                        {
                            toolName = root.GetProperty("tool").GetString() ?? "";
                            if (root.TryGetProperty("input", out var inp))
                            {
                                // Clone so we can use it after the doc is disposed.
                                input = inp.Clone();
                            }
                            if (root.TryGetProperty("toolUseId", out var tu) && tu.ValueKind == JsonValueKind.String)
                                toolUseId = tu.GetString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[PermissionPipeServer] malformed request: {Line}", line);
                        continue;
                    }

                    if (withdraw)
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

                    // Called here rather than on the task below: the broker
                    // registers the request before this returns, so a
                    // withdrawal on the next line always finds it.
                    var decisionTask = DecideAsync(id, toolName, input, toolUseId, ct);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var decision = await decisionTask.ConfigureAwait(false);
                            var response = SerializeDecision(id, decision);
                            await writeLock.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                await writer.WriteLineAsync(response).ConfigureAwait(false);
                            }
                            finally
                            {
                                writeLock.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[PermissionPipeServer] request {Id} failed", id);
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

    private async Task<PermissionDecision> DecideAsync(
        string id, string toolName, JsonElement input, string? toolUseId, CancellationToken ct)
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
