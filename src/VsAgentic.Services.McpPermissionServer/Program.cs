using System;
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
/// We also accept (but ignore) <c>notifications/initialized</c>.
/// </summary>
internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ToolName = "approval_prompt";

    private static StreamWriter? _pipeWriter;
    private static StreamReader? _pipeReader;
    private static readonly SemaphoreSlim _pipeLock = new SemaphoreSlim(1, 1);
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
                    await WriteResponseAsync(stdout, id!.Value, BuildInitializeResult()).ConfigureAwait(false);
                break;

            case "notifications/initialized":
            case "notifications/cancelled":
                // notifications: no response
                break;

            case "tools/list":
                if (hasId)
                    await WriteResponseAsync(stdout, id!.Value, BuildToolsListResult()).ConfigureAwait(false);
                break;

            case "tools/call":
                if (hasId)
                {
                    var result = await HandleToolCallAsync(root).ConfigureAwait(false);
                    await WriteResponseAsync(stdout, id!.Value, result).ConfigureAwait(false);
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

    private static string BuildToolsListResult()
    {
        // The schema mirrors what the Claude CLI's --permission-prompt-tool path expects:
        // a tool that takes (tool_name, input, tool_use_id) and returns
        // {"behavior":"allow","updatedInput":...} or {"behavior":"deny","message":"..."}
        // wrapped as MCP content of type "text" containing the JSON string.
        return @"{""tools"":[{""name"":""" + ToolName + @""",""description"":""Prompt the user for approval to use a tool"",""inputSchema"":{""type"":""object"",""properties"":{""tool_name"":{""type"":""string""},""input"":{""type"":""object""},""tool_use_id"":{""type"":""string""}},""required"":[""tool_name"",""input""]}}]}";
    }

    private static async Task<string> HandleToolCallAsync(JsonElement requestRoot)
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

        // Forward to the parent over the pipe and wait for the reply.
        var requestId = Guid.NewGuid().ToString("N");
        var pipeRequest = BuildPipeRequest(requestId, toolName, input);

        string pipeResponse;
        await _pipeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _pipeWriter!.WriteLineAsync(pipeRequest).ConfigureAwait(false);
            var line = await _pipeReader!.ReadLineAsync().ConfigureAwait(false);
            pipeResponse = line ?? "";
        }
        finally
        {
            _pipeLock.Release();
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
        await stdout.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(StreamWriter stdout, JsonElement id, int code, string message)
    {
        var sb = new StringBuilder();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":");
        sb.Append(id.GetRawText());
        sb.Append(",\"error\":{\"code\":").Append(code).Append(",\"message\":");
        sb.Append(JsonEscape(message));
        sb.Append("}}");
        await stdout.WriteLineAsync(sb.ToString()).ConfigureAwait(false);
    }
}
