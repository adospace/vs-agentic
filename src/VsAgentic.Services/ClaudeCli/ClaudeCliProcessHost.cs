using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Owns the long-lived <c>claude</c> CLI subprocess that we drive in
/// bidirectional stream-json mode.
///
///  - One process is started on first use; subsequent <c>SendMessageAsync</c>
///    calls reuse it. Session state lives in the CLI for the duration.
///  - All stdin writes go through a single <see cref="Channel{T}"/> serviced
///    by one writer task — there is no risk of two writers interleaving lines.
///  - Stdout is broadcast to a single subscriber via another channel; the
///    chat service is the consumer.
///  - The MCP permission helper exe is wired up via <c>--permission-prompt-tool</c>
///    plus an in-process named pipe (see <see cref="PermissionPipeServer"/>).
///  - On process death the host can be restarted with <see cref="EnsureStartedAsync"/>;
///    callers should pass the prior session id (if any) to <see cref="StartAsync"/>
///    to resume context.
/// </summary>
public sealed class ClaudeCliProcessHost : IDisposable
{
    private readonly VsAgenticOptions _options;
    private readonly IPermissionBroker _permissionBroker;
    private readonly ILogger _logger;

    private Process? _process;
    private Channel<string>? _stdinChannel;
    private Channel<JsonElement>? _stdoutChannel;
    private CancellationTokenSource? _runCts;
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _stderrTask;
    private PermissionPipeServer? _pipeServer;
    private string? _resumeSessionId;
    private string? _mcpConfigTempPath;

    private readonly object _lifecycleLock = new object();

    public ClaudeCliProcessHost(
        IOptions<VsAgenticOptions> options,
        IPermissionBroker permissionBroker,
        ILogger<ClaudeCliProcessHost> logger)
    {
        _options = options.Value;
        _permissionBroker = permissionBroker;
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// The channel reader the chat service pulls events from. Valid only after
    /// <see cref="EnsureStartedAsync"/> succeeds.
    /// </summary>
    public ChannelReader<JsonElement> EventReader => _stdoutChannel!.Reader;

    /// <summary>
    /// Set the session id to resume on the next start. Call before
    /// <see cref="EnsureStartedAsync"/>.
    /// </summary>
    public void SetResumeSessionId(string? sessionId)
    {
        _resumeSessionId = sessionId;
    }

    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning) return Task.CompletedTask;
            StartLocked();
            return Task.CompletedTask;
        }
    }

    private void StartLocked()
    {
        // Tear down anything stale from a prior crashed run.
        TearDownLocked();

        _runCts = new CancellationTokenSource();

        // Start the in-process pipe server before launching the CLI so the
        // MCP helper can connect immediately.
        _pipeServer = new PermissionPipeServer(_permissionBroker, _logger);
        _pipeServer.Start();

        var helperExePath = ResolveHelperExePath();
        if (!File.Exists(helperExePath))
        {
            _logger.LogError("[ClaudeCli] Permission helper exe not found at '{Path}'. Permission prompts will hang.", helperExePath);
        }
        else
        {
            _logger.LogInformation("[ClaudeCli] Permission helper exe: {Path}", helperExePath);
        }

        // Write the MCP config to a temp file. The CLI's --mcp-config flag
        // accepts a path more reliably than inline JSON, especially with the
        // quoting nightmare on Windows.
        var mcpConfigJson = BuildMcpConfigJson(helperExePath, _pipeServer.PipeName, _pipeServer.Secret);
        _mcpConfigTempPath = Path.Combine(Path.GetTempPath(), $"vsagentic-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(_mcpConfigTempPath, mcpConfigJson, new UTF8Encoding(false));
        _logger.LogInformation("[ClaudeCli] MCP config written to: {Path}", _mcpConfigTempPath);

        var args = BuildArguments(_mcpConfigTempPath);
        _logger.LogInformation("[ClaudeCli] Launching: {Exe} {Args}", _options.ClaudeCliPath, args);

        var psi = new ProcessStartInfo
        {
            FileName = _options.ClaudeCliPath,
            Arguments = args,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Strip any inherited API key so the CLI uses subscription auth.
        psi.EnvironmentVariables["ANTHROPIC_API_KEY"] = "";

        _stdinChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _stdoutChannel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Capture a local reference so the lambda completes THIS run's channel,
        // not a newer one created by a subsequent StartLocked() call.
        var channelForThisRun = _stdoutChannel;
        _process.Exited += (_, __) =>
        {
            _logger.LogInformation("[ClaudeCli] Process exited with code {Code}", _process?.ExitCode);
            // Tear down the channel so any blocked reader unblocks.
            try { channelForThisRun.Writer.TryComplete(); } catch { }
        };
        _process.Start();

        // Assign to a Win32 Job Object so the child is killed if the IDE
        // crashes or is force-terminated without a chance to run cleanup.
        ChildProcessTracker.AddProcess(_process);

        _writerTask = Task.Run(() => StdinWriterLoopAsync(_runCts.Token));
        _readerTask = Task.Run(() => StdoutReaderLoopAsync(_runCts.Token));
        _stderrTask = Task.Run(() => StderrLoopAsync(_runCts.Token));
    }

    private string BuildArguments(string mcpConfigPath)
    {
        var sb = new StringBuilder();
        // -p (print mode) is required for --permission-prompt-tool to take effect.
        // The stream-json input/output flags only apply in print mode.
        sb.Append("-p --input-format stream-json --output-format stream-json --verbose");

        var permFlag = _options.CliPermissionMode switch
        {
            CliPermissionMode.BypassPermissions => "bypassPermissions",
            CliPermissionMode.AcceptEdits => "acceptEdits",
            _ => "default",
        };
        sb.Append(" --permission-mode ");
        sb.Append(permFlag);

        // Wire the MCP permission tool so the CLI asks us before running gated tools.
        // --strict-mcp-config prevents user-level MCP config from polluting the session.
        sb.Append(" --permission-prompt-tool mcp__vsagentic__approval_prompt");
        sb.Append(" --strict-mcp-config");
        sb.Append(" --mcp-config ");
        sb.Append(EscapeArgument(mcpConfigPath));

        // Nudge Claude to actually use the AskUserQuestion tool instead of
        // inlining clarifying questions in plain text. The tool IS available
        // in the headless tool set (we verified via the system/init event),
        // but the default model behavior in print mode is to answer inline.
        sb.Append(" --append-system-prompt ");
        sb.Append(EscapeArgument(AskUserQuestionPromptNudge));

        if (!string.IsNullOrEmpty(_resumeSessionId))
        {
            sb.Append(" --resume ");
            sb.Append(EscapeArgument(_resumeSessionId!));
        }

        return sb.ToString();
    }

    private const string AskUserQuestionPromptNudge =
        "The host application renders the AskUserQuestion tool as an interactive " +
        "multiple-choice card. When a clarification naturally fits 2-4 discrete " +
        "options, prefer AskUserQuestion over inline plain-text questions. For " +
        "open-ended or multi-axis questions, plain prose is still the right choice.";

    private static string BuildMcpConfigJson(string helperExePath, string pipeName, string secret)
    {
        // The CLI spawns our helper exe as a stdio MCP server named "vsagentic"
        // and passes the pipe name + secret via env vars.
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VsAgentic", "logs");
        try { Directory.CreateDirectory(logDir); } catch { }

        var sb = new StringBuilder();
        sb.Append("{\"mcpServers\":{\"vsagentic\":{\"command\":");
        sb.Append(JsonSerializer.Serialize(helperExePath));
        sb.Append(",\"args\":[],\"env\":{\"VSAGENTIC_PERMISSION_PIPE\":");
        sb.Append(JsonSerializer.Serialize(pipeName));
        sb.Append(",\"VSAGENTIC_PERMISSION_SECRET\":");
        sb.Append(JsonSerializer.Serialize(secret));
        sb.Append(",\"VSAGENTIC_PERMISSION_LOG_DIR\":");
        sb.Append(JsonSerializer.Serialize(logDir));
        sb.Append("}}}}");
        return sb.ToString();
    }

    private string ResolveHelperExePath()
    {
        // The helper exe is copied next to VsAgentic.Services.dll by the
        // ProjectReference-as-content trick. Try the running assembly's directory first.
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, "vsagentic-mcp-permissions.exe");
        if (File.Exists(candidate)) return candidate;

        // Fallback: same directory as this assembly.
        var asmDir = Path.GetDirectoryName(typeof(ClaudeCliProcessHost).Assembly.Location);
        if (asmDir is not null)
        {
            candidate = Path.Combine(asmDir, "vsagentic-mcp-permissions.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // Last resort: assume PATH.
        _logger.LogWarning("[ClaudeCli] vsagentic-mcp-permissions.exe not found near assembly; falling back to PATH");
        return "vsagentic-mcp-permissions.exe";
    }

    /// <summary>
    /// Enqueue a JSON line for stdin. Safe to call from any thread.
    /// </summary>
    public ValueTask WriteLineAsync(string jsonLine, CancellationToken cancellationToken)
    {
        var ch = _stdinChannel ?? throw new InvalidOperationException("Process not started");
        return ch.Writer.WriteAsync(jsonLine, cancellationToken);
    }

    private async Task StdinWriterLoopAsync(CancellationToken ct)
    {
        try
        {
            var stdin = _process!.StandardInput;
            stdin.NewLine = "\n";
            var reader = _stdinChannel!.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    await stdin.WriteLineAsync(line).ConfigureAwait(false);
                    await stdin.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] stdin writer crashed");
        }
    }

    private async Task StdoutReaderLoopAsync(CancellationToken ct)
    {
        try
        {
            var stdout = _process!.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement evt;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    evt = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.LogTrace("[ClaudeCli] Skipping non-JSON line: {Line} ({Error})", line, ex.Message);
                    continue;
                }

                await _stdoutChannel!.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] stdout reader crashed");
        }
        finally
        {
            _stdoutChannel?.Writer.TryComplete();
        }
    }

    private async Task StderrLoopAsync(CancellationToken ct)
    {
        try
        {
            var stderr = _process!.StandardError;
            string? line;
            while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (ct.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.LogInformation("[ClaudeCli stderr] {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "[ClaudeCli] stderr loop ended");
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            TearDownLocked();
        }
    }

    private void TearDownLocked()
    {
        try { _runCts?.Cancel(); } catch { }
        try
        {
            if (_process is { HasExited: false })
            {
                try { _process.StandardInput.Close(); } catch { }
                try { _process.Kill(); } catch { }
            }
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        try { _pipeServer?.Dispose(); } catch { }
        try
        {
            if (!string.IsNullOrEmpty(_mcpConfigTempPath) && File.Exists(_mcpConfigTempPath))
                File.Delete(_mcpConfigTempPath);
        }
        catch { }
        _mcpConfigTempPath = null;
        _process = null;
        _pipeServer = null;
        _runCts = null;
        _writerTask = null;
        _readerTask = null;
        _stderrTask = null;
        _stdinChannel = null;
        _stdoutChannel = null;
    }

    public void Dispose() => Stop();

    private static string EscapeArgument(string arg)
    {
        // Windows command-line escaping rules (as parsed by the MS CRT and
        // most CLI runtimes including Node):
        //  - Backslashes are only special when followed by a double quote.
        //  - A run of N backslashes followed by " becomes 2N backslashes + \".
        //  - A run of N backslashes NOT followed by " stays as N backslashes.
        //  - To wrap an argument containing spaces, surround with " ".
        //
        // For a typical Windows path like C:\Users\foo\bar.json with no
        // embedded quotes, this means we just wrap in quotes — no doubling.
        if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return arg;

        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                // Double the pending backslashes, then escape the quote.
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
                backslashes = 0;
            }
        }
        // Trailing backslashes need doubling because they sit before the closing quote.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
