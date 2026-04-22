using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Hosts a named pipe inside the parent extension process. The MCP permission
/// helper exe (spawned by the Claude CLI as a stdio MCP server) connects back
/// to this pipe to forward permission requests it receives from the CLI.
///
/// Wire format: newline-delimited JSON, two shapes only.
///   client → server  {"id":"<guid>","tool":"Bash","input":{...}}
///   server → client  {"id":"<guid>","behavior":"allow","updatedInput":{...}}
///                    {"id":"<guid>","behavior":"deny","message":"..."}
///
/// One pipe instance per CLI process. Started by <c>ClaudeCliProcessHost</c>
/// before launching the CLI; the pipe name + shared secret are passed to the
/// MCP helper via env vars in the --mcp-config block.
/// </summary>
internal sealed class PermissionPipeServer : IDisposable
{
    private readonly IPermissionBroker _broker;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task? _acceptLoop;

    public string PipeName => _pipeName;
    public string Secret => _secret;

    public PermissionPipeServer(IPermissionBroker broker, ILogger logger)
    {
        _broker = broker;
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

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string id = "";
                    string toolName = "";
                    JsonElement input = default;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        id = root.GetProperty("id").GetString() ?? "";
                        toolName = root.GetProperty("tool").GetString() ?? "";
                        if (root.TryGetProperty("input", out var inp))
                        {
                            // Clone so we can use it after the doc is disposed.
                            input = inp.Clone();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[PermissionPipeServer] malformed request: {Line}", line);
                        continue;
                    }

                    // AskUserQuestion is itself a user-facing prompt rendered as a
                    // question card. Asking for permission first would show a confusing
                    // Allow/Deny banner with the raw questions JSON.
                    PermissionDecision decision;
                    if (toolName == "AskUserQuestion")
                    {
                        var inputJson = input.ValueKind == JsonValueKind.Undefined
                            ? "{}"
                            : input.GetRawText();
                        decision = PermissionDecision.Allow(inputJson);
                    }
                    else
                    {
                        var request = new PermissionRequest(id, toolName, input);
                        try
                        {
                            decision = await _broker.SubmitAsync(request, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[PermissionPipeServer] broker.SubmitAsync threw");
                            decision = PermissionDecision.Deny("Internal error");
                        }
                    }

                    var response = SerializeDecision(id, decision);
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PermissionPipeServer] connection handler crashed");
        }
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
