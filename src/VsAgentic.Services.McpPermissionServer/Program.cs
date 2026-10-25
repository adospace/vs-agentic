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
/// Tiny stdio MCP server, <c>vsagentic</c>. It exposes <c>approval_prompt</c>,
/// for use with <c>claude --permission-prompt-tool mcp__vsagentic__approval_prompt</c>,
/// and the IDE tools the extension registers (the Error List, the editor, ...).
///
/// Every tool call is forwarded over a named pipe (set via
/// <c>VSAGENTIC_PERMISSION_PIPE</c> env var, with the shared secret in
/// <c>VSAGENTIC_PERMISSION_SECRET</c>) to the parent VsAgentic extension,
/// which surfaces permission requests to the user and answers IDE tools from
/// Visual Studio. The wire format is described on <c>PermissionPipeServer</c>.
///
/// MCP transport: JSON-RPC 2.0, one message per line on stdin/stdout.
/// Methods we implement: <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>.
/// We also accept (but ignore) <c>notifications/initialized</c>.
/// </summary>
internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ToolName = "approval_prompt";

    // The extension answers this from memory; waiting longer would only delay
    // the CLI's start.
    private static readonly TimeSpan ToolsListTimeout = TimeSpan.FromSeconds(5);

    private static StreamWriter? _pipeWriter;
    private static StreamReader? _pipeReader;
    private static volatile bool _pipeBroken;

    // Requests to the parent overlap; each waits for the response with its id.
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending =
        new ConcurrentDictionary<string, TaskCompletionSource<string>>();
    private static readonly SemaphoreSlim _pipeWriteLock = new SemaphoreSlim(1, 1);

    // Permission prompts still go one at a time, as they always have: the
    // banner shows one request, and an IDE tool call no longer queues behind it.
    private static readonly SemaphoreSlim _permissionLock = new SemaphoreSlim(1, 1);

    private static readonly SemaphoreSlim _stdoutLock = new SemaphoreSlim(1, 1);
    private static readonly object _logLock = new object();
    private static string? _logFilePath;

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

        _ = Task.Run(PipeReaderLoopAsync);
    }

    /// <summary>
    /// Hands each response from the parent to the request with the same id.
    /// When the pipe closes, every request still waiting fails at once rather
    /// than hang the CLI.
    /// </summary>
    private static async Task PipeReaderLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _pipeReader!.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string? id = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idEl))
                        id = idEl.GetString();
                }
                catch (JsonException ex)
                {
                    Log($"malformed pipe response: {ex.Message}");
                }

                if (id != null && _pending.TryRemove(id, out var waiter))
                    waiter.TrySetResult(line);
                else
                    Log($"pipe response with no waiting request: {line}");
            }
            Log("pipe closed by parent");
        }
        catch (Exception ex)
        {
            Log($"pipe reader error: {ex.Message}");
        }

        _pipeBroken = true;
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
                waiter.TrySetException(new IOException("pipe closed"));
        }
    }

    /// <summary>Sends one request to the parent and returns its response line.</summary>
    private static async Task<string> SendToParentAsync(string id, string requestJson, TimeSpan? timeout = null)
    {
        if (_pipeBroken)
            throw new IOException("pipe closed");

        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        await _pipeWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _pipeWriter!.WriteLineAsync(requestJson).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _pipeBroken = true;
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _pipeWriteLock.Release();
        }

        if (timeout is { } limit &&
            await Task.WhenAny(waiter.Task, Task.Delay(limit)).ConfigureAwait(false) != waiter.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException();
        }

        return await waiter.Task.ConfigureAwait(false);
    }

    private static async Task HandleRequestAsync(string requestLine, StreamWriter stdout)
    {
        using var doc = JsonDocument.Parse(requestLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodEl)) return;
        var method = methodEl.GetString();
        var id = root.TryGetProperty("id", out var idEl) ? (JsonElement?)idEl.Clone() : null;
        var hasId = id.HasValue && id.Value.ValueKind != JsonValueKind.Null;

        switch (method)
        {
            case "initialize":
                if (hasId)
                    await WriteResponseAsync(stdout, id!.Value, BuildInitializeResult()).ConfigureAwait(false);
                break;

            case "notifications/initialized":
            case "notifications/cancelled":
                // notifications: no response
                break;

            case "tools/list":
                if (hasId)
                    await WriteResponseAsync(stdout, id!.Value, await BuildToolsListResultAsync().ConfigureAwait(false)).ConfigureAwait(false);
                break;

            case "tools/call":
                if (hasId)
                {
                    // Not awaited: the next request on stdin may be a permission
                    // prompt that must not wait for a slow IDE tool.
                    var request = root.Clone();
                    _ = Task.Run(async () =>
                    {
                        string result;
                        try
                        {
                            result = await HandleToolCallAsync(request).ConfigureAwait(false);
                        }
                        catch (IOException ex)
                        {
                            // Pipe to parent broke — return an error to the CLI so it
                            // doesn't hang forever waiting for a response that will
                            // never arrive.
                            Log($"tools/call pipe error, returning deny: {ex.Message}");
                            result = BuildToolErrorContent("Permission pipe disconnected");
                        }
                        catch (Exception ex)
                        {
                            Log($"tools/call error: {ex}");
                            result = BuildToolErrorContent("Internal error");
                        }
                        await WriteResponseAsync(stdout, id!.Value, result).ConfigureAwait(false);
                    });
                }
                break;

            default:
                if (hasId)
                    await WriteErrorAsync(stdout, id!.Value, -32601, "Method not found").ConfigureAwait(false);
                break;
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

    private static async Task<string> BuildToolsListResultAsync()
    {
        // The schema mirrors what the Claude CLI's --permission-prompt-tool path expects:
        // a tool that takes (tool_name, input, tool_use_id) and returns
        // {"behavior":"allow","updatedInput":...} or {"behavior":"deny","message":"..."}
        // wrapped as MCP content of type "text" containing the JSON string.
        var approvalTool = @"{""name"":""" + ToolName + @""",""description"":""Prompt the user for approval to use a tool"",""inputSchema"":{""type"":""object"",""properties"":{""tool_name"":{""type"":""string""},""input"":{""type"":""object""},""tool_use_id"":{""type"":""string""}},""required"":[""tool_name"",""input""]}}";

        // The IDE tools come from the extension. Without them the permission
        // tool still works, so a failure here only costs the IDE tools.
        var ideTools = "";
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var response = await SendToParentAsync(
                requestId,
                "{\"id\":" + JsonEscape(requestId) + ",\"type\":\"tools/list\"}",
                ToolsListTimeout).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var tool in tools.EnumerateArray())
                    sb.Append(',').Append(tool.GetRawText());
                ideTools = sb.ToString();
            }
        }
        catch (Exception ex)
        {
            Log($"could not get the IDE tools: {ex.Message}");
        }

        return "{\"tools\":[" + approvalTool + ideTools + "]}";
    }

    private static async Task<string> HandleToolCallAsync(JsonElement requestRoot)
    {
        // params.name is "approval_prompt" or an IDE tool; params.arguments has the fields.
        if (!requestRoot.TryGetProperty("params", out var paramsEl))
            return BuildToolErrorContent("missing params");

        var name = paramsEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrEmpty(name))
            return BuildToolErrorContent("missing tool name");

        if (name != ToolName)
            return await HandleIdeToolCallAsync(name!, paramsEl).ConfigureAwait(false);

        if (!paramsEl.TryGetProperty("arguments", out var argsEl))
            return BuildToolErrorContent("missing arguments");

        var toolName = argsEl.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        JsonElement input = argsEl.TryGetProperty("input", out var inp) ? inp : default;

        // If a prior call already discovered the pipe is broken, fail fast.
        if (_pipeBroken)
            return BuildToolErrorContent("Permission pipe disconnected");

        // Forward to the parent over the pipe and wait for the reply.
        var requestId = Guid.NewGuid().ToString("N");
        var pipeRequest = BuildPipeRequest(requestId, toolName, input);

        string pipeResponse;
        await _permissionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            pipeResponse = await SendToParentAsync(requestId, pipeRequest).ConfigureAwait(false);
        }
        finally
        {
            _permissionLock.Release();
        }

        if (string.IsNullOrEmpty(pipeResponse))
            return BuildToolErrorContent("pipe closed");

        // The pipe response is already in the shape the CLI expects
        // ({"behavior":..., "updatedInput":..., "message":...} but with an extra "id"
        // field we should strip). Repackage as MCP tool result content (text JSON).
        string innerJson = StripIdField(pipeResponse);
        return BuildToolSuccessContent(innerJson);
    }

    private static string BuildPipeRequest(string id, string toolName, JsonElement input)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":");
        sb.Append(JsonEscape(id));
        sb.Append(",\"tool\":");
        sb.Append(JsonEscape(toolName));
        sb.Append(",\"input\":");
        sb.Append(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText());
        sb.Append("}");
        return sb.ToString();
    }

    /// <summary>
    /// Runs an IDE tool in the extension and wraps its text as MCP content.
    /// The extension never throws back; a failure arrives as an error result.
    /// </summary>
    private static async Task<string> HandleIdeToolCallAsync(string name, JsonElement paramsEl)
    {
        if (_pipeBroken)
            return BuildIdeToolResult("Visual Studio is not reachable.", isError: true);

        var requestId = Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("{\"id\":").Append(JsonEscape(requestId));
        sb.Append(",\"type\":\"tools/call\",\"name\":").Append(JsonEscape(name));
        sb.Append(",\"arguments\":");
        sb.Append(paramsEl.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
            ? args.GetRawText()
            : "{}");
        sb.Append('}');

        var response = await SendToParentAsync(requestId, sb.ToString()).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var isError = root.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        return BuildIdeToolResult(text, isError);
    }

    private static string BuildIdeToolResult(string text, bool isError)
    {
        var sb = new StringBuilder();
        sb.Append("{\"content\":[{\"type\":\"text\",\"text\":");
        sb.Append(JsonEscape(text));
        sb.Append("}]");
        if (isError) sb.Append(",\"isError\":true");
        sb.Append('}');
        return sb.ToString();
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

    private static async Task WriteResponseAsync(StreamWriter stdout, JsonElement id, string resultJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
        sb.Append(id.GetRawText());
        sb.Append(",\"result\":");
        sb.Append(resultJson);
        sb.Append("}");
        var line = sb.ToString();
        Log($"-> stdout: {line}");
        await WriteStdoutAsync(stdout, line).ConfigureAwait(false);
    }

    // Tool calls answer from their own tasks, so two responses can be ready
    // at once; each must reach the CLI as one whole line.
    private static async Task WriteStdoutAsync(StreamWriter stdout, string line)
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

    private static async Task WriteErrorAsync(StreamWriter stdout, JsonElement id, int code, string message)
    {
        var sb = new StringBuilder();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
        sb.Append(id.GetRawText());
        sb.Append(",\"error\":{\"code\":").Append(code).Append(",\"message\":");
        sb.Append(JsonEscape(message));
        sb.Append("}}");
        await WriteStdoutAsync(stdout, sb.ToString()).ConfigureAwait(false);
    }
}
