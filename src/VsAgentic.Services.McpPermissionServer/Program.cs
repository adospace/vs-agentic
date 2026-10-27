using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.McpPermissionServer;

/// <summary>
/// Tiny stdio MCP server that exposes a single tool, <c>approval_prompt</c>,
/// for use with <c>claude --permission-prompt-tool mcp__vsagentic__approval_prompt</c>.
///
/// When the CLI calls the tool, this process forwards the request over a
/// named pipe (set via <c>VSAGENTIC_PERMISSION_PIPE</c> env var, with the
/// shared secret in <c>VSAGENTIC_PERMISSION_SECRET</c>) to the parent
/// VsAgentic extension, which surfaces it to the user and replies with
/// allow/deny.
///
/// MCP transport: JSON-RPC 2.0, one message per line on stdin/stdout.
/// Methods we implement: <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>.
/// We accept <c>notifications/initialized</c> and act on
/// <c>notifications/cancelled</c>.
///
/// Several calls can be open at once: subagents running in parallel each ask
/// on their own. The stdin loop therefore never waits for a call, and the
/// pipe matches replies to requests by id.
/// </summary>
internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ToolName = "approval_prompt";

    private static StreamWriter? _pipeWriter;
    private static StreamReader? _pipeReader;
    private static volatile bool _pipeBroken;
    private static readonly SemaphoreSlim _pipeWriteLock = new SemaphoreSlim(1, 1);
    private static readonly SemaphoreSlim _stdoutLock = new SemaphoreSlim(1, 1);
    private static readonly object _logLock = new object();
    private static string? _logFilePath;

    // Pipe request id -> the parent's reply line, once it arrives.
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingReplies =
        new ConcurrentDictionary<string, TaskCompletionSource<string>>();

    // JSON-RPC id of an open tools/call (raw JSON text) -> its pipe request id.
    // A call whose entry is gone when the reply arrives was cancelled by the CLI.
    private static readonly ConcurrentDictionary<string, string> _openCalls =
        new ConcurrentDictionary<string, string>();

    private static void Log(string message)
    {
        try
        {
            if (_logFilePath == null) return;
            lock (_logLock)
            {
                File.AppendAllText(_logFilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
        }
        catch { }
    }

    /// <summary>
    /// Manual JSON string escaper. Used in place of <c>JsonSerializer.Serialize(string)</c>
    /// because that path triggers <c>DefaultJsonTypeInfoResolver</c> which has a hard
    /// dependency on a specific version of <c>Microsoft.Bcl.AsyncInterfaces</c> that
    /// fails to load on net472 inside the Claude CLI's spawn environment.
    /// Wraps the result in surrounding double quotes.
    /// </summary>
    private static string JsonEscape(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                        sb.AppendFormat("\\u{0:X4}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static async Task<int> Main(string[] args)
    {
        // File logging: stdio is reserved for MCP, so anything we want to see
        // about the helper's lifecycle has to go to a file. Place it next to
        // the parent's log directory if we can guess it; fall back to %TEMP%.
        try
        {
            var logDir = Environment.GetEnvironmentVariable("VSAGENTIC_PERMISSION_LOG_DIR");
            if (string.IsNullOrEmpty(logDir))
                logDir = Path.GetTempPath();
            _logFilePath = Path.Combine(logDir, $"vsagentic-mcp-helper-{DateTime.Now:yyyyMMdd}.log");
        }
        catch { }

        Log($"--- helper started, pid={System.Diagnostics.Process.GetCurrentProcess().Id} ---");
        Log($"args: {string.Join(" ", args)}");

        try
        {
            var pipeName = Environment.GetEnvironmentVariable("VSAGENTIC_PERMISSION_PIPE");
            var secret = Environment.GetEnvironmentVariable("VSAGENTIC_PERMISSION_SECRET");
            Log($"VSAGENTIC_PERMISSION_PIPE={pipeName}");
            Log($"VSAGENTIC_PERMISSION_SECRET={(string.IsNullOrEmpty(secret) ? "(empty)" : "***")}");

            if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(secret))
            {
                Log("ERROR: env vars missing, exiting with code 2");
                Console.Error.WriteLine("VSAGENTIC_PERMISSION_PIPE / _SECRET env vars missing");
                return 2;
            }

            Log("connecting to pipe...");
            await ConnectPipeAsync(pipeName!, secret!).ConfigureAwait(false);
            Log("pipe connected, handshake sent");
            _ = Task.Run(PipeReadLoopAsync);

            // The Console default streams use the OEM encoding on Windows; force UTF-8.
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            Log("entering MCP request loop");
            string? line;
            while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Log($"<- stdin: {line}");
                try
                {
                    await HandleRequestAsync(line, stdout).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"handler error: {ex}");
                    Console.Error.WriteLine("[mcp-permissions] handler error: " + ex);
                }
            }

            Log("stdin closed, exiting normally");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            Console.Error.WriteLine("[mcp-permissions] fatal: " + ex);
            return 1;
        }
    }

    private static async Task ConnectPipeAsync(string pipeName, string secret)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.Run(() => client.Connect(10_000)).ConfigureAwait(false);

        _pipeWriter = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _pipeReader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await _pipeWriter.WriteLineAsync(secret).ConfigureAwait(false);
    }

    /// <summary>
    /// The only reader of the pipe. Hands each reply to the call waiting for
    /// its id. When the pipe closes, every waiting call gets an empty reply,
    /// which it turns into a deny.
    /// </summary>
    private static async Task PipeReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _pipeReader!.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                var id = ReadStringProperty(line, "id");
                if (id != null && _pendingReplies.TryRemove(id, out var waiter))
                    waiter.TrySetResult(line);
                else
                    Log($"pipe reply for unknown id: {line}");
            }
            Log("pipe closed by parent");
        }
        catch (Exception ex)
        {
            Log($"pipe read error: {ex.Message}");
        }

        _pipeBroken = true;
        foreach (var id in _pendingReplies.Keys)
        {
            if (_pendingReplies.TryRemove(id, out var waiter))
                waiter.TrySetResult("");
        }
    }

    private static async Task HandleRequestAsync(string requestLine, StreamWriter stdout)
    {
        using var doc = JsonDocument.Parse(requestLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodEl)) return;
        var method = methodEl.GetString();
        var id = root.TryGetProperty("id", out var idEl) ? (JsonElement?)idEl : null;
        var hasId = id.HasValue && id.Value.ValueKind != JsonValueKind.Null;

        switch (method)
        {
            case "initialize":
                if (hasId)
                    await WriteResponseAsync(stdout, id!.Value.GetRawText(), BuildInitializeResult()).ConfigureAwait(false);
                break;

            case "notifications/initialized":
                // notifications: no response
                break;

            case "notifications/cancelled":
                // The CLI no longer wants the answer: the turn was interrupted,
                // the subagent stopped, or the request timed out on its side.
                if (root.TryGetProperty("params", out var cancelParams) &&
                    cancelParams.TryGetProperty("requestId", out var cancelledId))
                {
                    await CancelCallAsync(cancelledId.GetRawText()).ConfigureAwait(false);
                }
                break;

            case "tools/list":
                if (hasId)
                    await WriteResponseAsync(stdout, id!.Value.GetRawText(), BuildToolsListResult()).ConfigureAwait(false);
                break;

            case "tools/call":
                if (hasId)
                {
                    // Not awaited: the loop must keep reading, both for calls
                    // from other subagents and for the CLI cancelling this one.
                    // Registered here, before the next line is read, so a
                    // cancel that follows right away finds the call.
                    var rpcId = id!.Value.GetRawText();
                    var requestId = Guid.NewGuid().ToString("N");
                    _openCalls[rpcId] = requestId;
                    var callRoot = root.Clone();
                    _ = Task.Run(() => RunToolCallAsync(rpcId, requestId, callRoot, stdout));
                }
                break;

            default:
                if (hasId)
                    await WriteErrorAsync(stdout, id!.Value.GetRawText(), -32601, "Method not found").ConfigureAwait(false);
                break;
        }
    }

    private static async Task RunToolCallAsync(string rpcId, string requestId, JsonElement requestRoot, StreamWriter stdout)
    {
        try
        {
            string result;
            try
            {
                result = await HandleToolCallAsync(rpcId, requestId, requestRoot).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // Pipe to parent broke — return an error to the CLI so it
                // doesn't hang forever waiting for a response that will
                // never arrive.
                Log($"tools/call pipe error, returning deny: {ex.Message}");
                result = BuildToolErrorContent("Permission pipe disconnected");
            }

            // A call the CLI cancelled gets no response (MCP: the receiver
            // should not answer a cancelled request).
            if (!_openCalls.TryRemove(rpcId, out _))
            {
                Log($"tools/call {rpcId} was cancelled, response dropped");
                return;
            }
            await WriteResponseAsync(stdout, rpcId, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"tools/call {rpcId} error: {ex}");
        }
    }

    private static string BuildInitializeResult()
    {
        // Minimal initialize result; advertise tools capability only.
        var sb = new StringBuilder();
        sb.Append("{\"protocolVersion\":\"").Append(ProtocolVersion).Append("\"");
        sb.Append(",\"capabilities\":{\"tools\":{}}");
        sb.Append(",\"serverInfo\":{\"name\":\"vsagentic-permissions\",\"version\":\"1.0.0\"}}");
        return sb.ToString();
    }

    private static string BuildToolsListResult()
    {
        // The schema mirrors what the Claude CLI's --permission-prompt-tool path expects:
        // a tool that takes (tool_name, input, tool_use_id) and returns
        // {"behavior":"allow","updatedInput":...} or {"behavior":"deny","message":"..."}
        // wrapped as MCP content of type "text" containing the JSON string.
        return @"{""tools"":[{""name"":""" + ToolName + @""",""description"":""Prompt the user for approval to use a tool"",""inputSchema"":{""type"":""object"",""properties"":{""tool_name"":{""type"":""string""},""input"":{""type"":""object""},""tool_use_id"":{""type"":""string""}},""required"":[""tool_name"",""input""]}}]}";
    }

    private static async Task<string> HandleToolCallAsync(string rpcId, string requestId, JsonElement requestRoot)
    {
        // params.name should be "approval_prompt"; params.arguments has the fields.
        if (!requestRoot.TryGetProperty("params", out var paramsEl))
            return BuildToolErrorContent("missing params");

        var name = paramsEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (name != ToolName)
            return BuildToolErrorContent("unknown tool: " + name);

        if (!paramsEl.TryGetProperty("arguments", out var argsEl))
            return BuildToolErrorContent("missing arguments");

        var toolName = argsEl.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        JsonElement input = argsEl.TryGetProperty("input", out var inp) ? inp : default;
        var toolUseId = argsEl.TryGetProperty("tool_use_id", out var tu) && tu.ValueKind == JsonValueKind.String
            ? tu.GetString()
            : null;

        // Forward to the parent over the pipe and wait for the reply.
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReplies[requestId] = reply;

        // Checked after registering, so a pipe that breaks in between still
        // settles this call through the read loop's cleanup.
        if (_pipeBroken)
        {
            _pendingReplies.TryRemove(requestId, out _);
            return BuildToolErrorContent("Permission pipe disconnected");
        }

        try
        {
            await WritePipeLineAsync(BuildPipeRequest(requestId, toolName, input, toolUseId)).ConfigureAwait(false);
        }
        catch
        {
            _pendingReplies.TryRemove(requestId, out _);
            throw;
        }

        // A cancel that came in before the request reached the parent found
        // nothing to take down there. Send it again now that it can.
        if (!_openCalls.ContainsKey(rpcId))
            await SendWithdrawAsync(requestId).ConfigureAwait(false);

        var pipeResponse = await reply.Task.ConfigureAwait(false);

        if (string.IsNullOrEmpty(pipeResponse))
            return BuildToolErrorContent("pipe closed");

        // The pipe response is already in the shape the CLI expects
        // ({"behavior":..., "updatedInput":..., "message":...} but with an extra "id"
        // field we should strip). Repackage as MCP tool result content (text JSON).
        string innerJson = StripIdField(pipeResponse);
        return BuildToolSuccessContent(innerJson);
    }

    /// <summary>
    /// Tells the parent to take the prompt down. The parent still answers it,
    /// which settles the waiting call; <see cref="RunToolCallAsync"/> then
    /// drops that answer because the call is no longer open.
    /// </summary>
    private static async Task CancelCallAsync(string rpcId)
    {
        if (!_openCalls.TryRemove(rpcId, out var requestId))
        {
            Log($"cancel for unknown or finished call {rpcId}");
            return;
        }

        Log($"tools/call {rpcId} cancelled by the CLI, withdrawing {requestId}");
        await SendWithdrawAsync(requestId).ConfigureAwait(false);
    }

    private static async Task SendWithdrawAsync(string requestId)
    {
        if (_pipeBroken) return;
        try
        {
            await WritePipeLineAsync("{\"id\":" + JsonEscape(requestId) + ",\"cancel\":true}").ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log($"withdraw {requestId} failed: {ex.Message}");
        }
    }

    private static async Task WritePipeLineAsync(string line)
    {
        await _pipeWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _pipeWriter!.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _pipeBroken = true;
            throw;
        }
        finally
        {
            _pipeWriteLock.Release();
        }
    }

    private static string BuildPipeRequest(string id, string toolName, JsonElement input, string? toolUseId)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":");
        sb.Append(JsonEscape(id));
        sb.Append(",\"tool\":");
        sb.Append(JsonEscape(toolName));
        sb.Append(",\"input\":");
        sb.Append(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText());
        if (toolUseId != null)
        {
            sb.Append(",\"toolUseId\":");
            sb.Append(JsonEscape(toolUseId));
        }
        sb.Append("}");
        return sb.ToString();
    }

    private static string? ReadStringProperty(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(name, out var el) &&
                   el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StripIdField(string json)
    {
        // Re-emit by hand. We can't use Utf8JsonWriter here because it pulls in
        // the same Microsoft.Bcl.AsyncInterfaces shim that breaks JsonSerializer.
        // JsonDocument.Parse + GetRawText() per property is safe.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return json;

            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "id") continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonEscape(prop.Name));
                sb.Append(':');
                sb.Append(prop.Value.GetRawText());
            }
            sb.Append('}');
            return sb.ToString();
        }
        catch
        {
            return json;
        }
    }

    private static string BuildToolSuccessContent(string innerJson)
    {
        // MCP tool_call result with content of type text containing the decision JSON.
        var sb = new StringBuilder();
        sb.Append("{\"content\":[{\"type\":\"text\",\"text\":");
        sb.Append(JsonEscape(innerJson));
        sb.Append("}]}");
        return sb.ToString();
    }

    private static string BuildToolErrorContent(string message)
    {
        var sb = new StringBuilder();
        sb.Append("{\"content\":[{\"type\":\"text\",\"text\":");
        sb.Append(JsonEscape("{\"behavior\":\"deny\",\"message\":\"" + message.Replace("\"", "\\\"") + "\"}"));
        sb.Append("}],\"isError\":true}");
        return sb.ToString();
    }

    private static async Task WriteResponseAsync(StreamWriter stdout, string rawId, string resultJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
        sb.Append(rawId);
        sb.Append(",\"result\":");
        sb.Append(resultJson);
        sb.Append("}");
        var line = sb.ToString();
        Log($"-> stdout: {line}");
        await WriteStdoutLineAsync(stdout, line).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(StreamWriter stdout, string rawId, int code, string message)
    {
        var sb = new StringBuilder();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
        sb.Append(rawId);
        sb.Append(",\"error\":{\"code\":").Append(code).Append(",\"message\":");
        sb.Append(JsonEscape(message));
        sb.Append("}}");
        await WriteStdoutLineAsync(stdout, sb.ToString()).ConfigureAwait(false);
    }

    // Calls finish on their own tasks, so two answers can be ready at once.
    private static async Task WriteStdoutLineAsync(StreamWriter stdout, string line)
    {
        await _stdoutLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stdout.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally
        {
            _stdoutLock.Release();
        }
    }
}
