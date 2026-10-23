using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
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
    private readonly IUserQuestionBroker _questionBroker;
    private readonly ILogger _logger;

    private Process? _process;
    private Channel<string>? _stdinChannel;
    private Channel<JsonElement>? _stdoutChannel;
    private CancellationTokenSource? _runCts;
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _stderrTask;
    private PermissionPipeServer? _pipeServer;
    private ProcessTreeJob? _processTree;
    private string? _resumeSessionId;
    private string? _mcpConfigTempPath;

    // The shutdown of the previous run. A new run waits for it, because the
    // old CLI may still be writing the conversation the new one resumes.
    private Task _shutdown = Task.CompletedTask;

    // Measured with CLI 2.1.296: an idle CLI exits about 0.7 s after stdin
    // closes. Mid-turn, or with a background command running, it does not
    // exit at all, so waiting longer gains nothing.
    private const int GracefulExitTimeoutMs = 2000;

    private readonly object _lifecycleLock = new object();

    public ClaudeCliProcessHost(
        IOptions<VsAgenticOptions> options,
        IPermissionBroker permissionBroker,
        IUserQuestionBroker questionBroker,
        ILogger<ClaudeCliProcessHost> logger)
    {
        _options = options.Value;
        _permissionBroker = permissionBroker;
        _questionBroker = questionBroker;
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

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_lifecycleLock)
        {
            if (IsRunning) return;
            shutdown = _shutdown;
        }

        await shutdown.ConfigureAwait(false);

        lock (_lifecycleLock)
        {
            if (IsRunning) return;
            StartLocked();
        }
    }

    private void StartLocked()
    {
        // Tear down anything stale from a prior crashed run.
        TearDownLocked();

        _runCts = new CancellationTokenSource();

        // Start the in-process pipe server before launching the CLI so the
        // MCP helper can connect immediately.
        _pipeServer = new PermissionPipeServer(_permissionBroker, _questionBroker, _logger);
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

        ClaudeCliChatService.UseSubscriptionAuth(psi);

        // Without this, a CLI that refuses to start (bad proxy URL, missing
        // shell, a session held elsewhere ...) explains itself on stderr only.
        // With it, the CLI also writes an error result with the same text and
        // a startup_failure_reason before it exits.
        psi.EnvironmentVariables["CLAUDE_CODE_STARTUP_FAILURE_RESULTS"] = "1";

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
        // Capture local references so the callbacks finish THIS run, not a
        // newer one created by a subsequent StartLocked() call.
        var process = _process;
        var channelForThisRun = _stdoutChannel;
        var stderrTail = new StderrTail();
        var runToken = _runCts.Token;
        Task stderrTask = Task.CompletedTask;
        process.Exited += (_, __) =>
        {
            _logger.LogInformation("[ClaudeCli] Process exited with code {Code}", TryGetExitCode(process));
            // The stdout reader normally completes the channel at end of
            // stream, after the CLI's last lines. Completing it here at once
            // could drop them, so this only covers a pipe that stays open
            // because a grandchild inherited it.
            _ = Task.Delay(ExitDrainTimeoutMs).ContinueWith(
                t => CompleteRun(channelForThisRun, process, stderrTask, stderrTail, runToken),
                TaskScheduler.Default);
        };
        process.Start();

        // Assign to a Win32 Job Object so the child is killed if the IDE
        // crashes or is force-terminated without a chance to run cleanup.
        ChildProcessTracker.AddProcess(process);
        _processTree = ProcessTreeJob.TryCreate(process);
        if (_processTree == null)
            _logger.LogWarning("[ClaudeCli] No job object for this run; stopping it ends the CLI but not its child processes");

        _writerTask = Task.Run(() => StdinWriterLoopAsync(runToken));
        _stderrTask = stderrTask = Task.Run(() => StderrLoopAsync(process, stderrTail, runToken));
        _readerTask = Task.Run(() => StdoutReaderLoopAsync(process, channelForThisRun, stderrTask, stderrTail, runToken));
    }

    // How long the end of a run waits for the process to exit and for stderr
    // to drain before it reports the exit.
    private const int ExitDrainTimeoutMs = 2000;

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    /// <summary>
    /// Completes the event channel of one run. A run we stopped ends quietly.
    /// A run that ended on its own ends with <see cref="CliProcessExitedException"/>,
    /// so the chat service can explain an exit the CLI did not explain itself.
    /// </summary>
    private void CompleteRun(Channel<JsonElement> channel, Process process, Task stderrTask, StderrTail stderrTail, CancellationToken runToken)
    {
        if (runToken.IsCancellationRequested)
        {
            channel.Writer.TryComplete();
            return;
        }

        int? exitCode = null;
        try
        {
            if (process.WaitForExit(ExitDrainTimeoutMs)) exitCode = process.ExitCode;
        }
        catch { }
        try { stderrTask.Wait(ExitDrainTimeoutMs); } catch { }

        if (channel.Writer.TryComplete(new CliProcessExitedException(exitCode, stderrTail.Text)))
            _logger.LogWarning("[ClaudeCli] CLI ended on its own (exit code {Code})", exitCode);
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

        // Model and effort are start-up flags. A change in the chat window goes
        // to a running process as a control request instead (see
        // ClaudeCliChatService.ApplyModelAndEffort); these flags cover every
        // start after that.
        //
        // Both are sent only when the user has picked a value. An empty model
        // leaves the CLI's own choice alone, and its init event reports what it
        // settled on. Effort is never reported back, so the dropdown can only
        // say Default in that case — but a CLI older than --effort refuses to
        // start when it sees the flag, and a working chat is worth more than a
        // named level.
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            sb.Append(" --model ");
            sb.Append(EscapeArgument(_options.Model.Trim()));
        }

        if (_options.Effort != ClaudeEffort.Default)
        {
            sb.Append(" --effort ");
            sb.Append(_options.Effort.ToCliValue());
        }

        // Wire the MCP permission tool so the CLI asks us before running gated tools.
        // --strict-mcp-config limits the session to our server. Without it the
        // CLI also loads the user's own servers (user, project .mcp.json,
        // claude.ai connectors), and ours is added alongside them.
        sb.Append(" --permission-prompt-tool mcp__").Append(PermissionServerName).Append("__approval_prompt");
        if (!_options.UseClaudeMcpServers)
            sb.Append(" --strict-mcp-config");
        sb.Append(" --mcp-config ");
        sb.Append(EscapeArgument(mcpConfigPath));

        // --resume must come BEFORE --append-system-prompt.  When the
        // target is a .cmd/.bat file, .NET spawns cmd.exe which can split
        // the argument string at embedded newlines, silently dropping
        // everything that follows.  Placing --resume early guarantees it
        // is always parsed correctly even if a future prompt adds newlines.
        if (!string.IsNullOrEmpty(_resumeSessionId))
        {
            sb.Append(" --resume ");
            sb.Append(EscapeArgument(_resumeSessionId!));
        }

        // Nudge Claude to actually use the AskUserQuestion tool instead of
        // inlining clarifying questions in plain text. The tool IS available
        // in the headless tool set (we verified via the system/init event),
        // but the default model behavior in print mode is to answer inline.
        sb.Append(" --append-system-prompt ");
        sb.Append(EscapeArgument(AskUserQuestionPromptNudge.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')));

        return sb.ToString();
    }

    private const string AskUserQuestionPromptNudge =
        "The host application renders the AskUserQuestion tool as an interactive " +
        "multiple-choice card. When a clarification naturally fits 2-4 discrete " +
        "options, prefer AskUserQuestion over inline plain-text questions. For " +
        "open-ended or multi-axis questions, plain prose is still the right choice.";

    /// <summary>The name our MCP helper is registered under in <c>--mcp-config</c>.</summary>
    internal const string PermissionServerName = "vsagentic";

    /// <summary>Where the MCP helper writes its log.</summary>
    internal static string HelperLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VsAgentic", "logs");

    private static string BuildMcpConfigJson(string helperExePath, string pipeName, string secret)
    {
        // The CLI spawns our helper exe as a stdio MCP server named "vsagentic"
        // and passes the pipe name + secret via env vars.
        var logDir = HelperLogDirectory;
        try { Directory.CreateDirectory(logDir); } catch { }

        var sb = new StringBuilder();
        sb.Append("{\"mcpServers\":{");
        sb.Append(JsonSerializer.Serialize(PermissionServerName));
        sb.Append(":{\"command\":");
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
        // The helper's entire bin folder (exe + its net472 dependencies like
        // System.Text.Json, Microsoft.Bcl.AsyncInterfaces, ...) is embedded in
        // this assembly as a zip so VSIX packaging can't lose it. We extract
        // into a per-content-hash subdirectory of %LOCALAPPDATA%\VsAgentic\helpers
        // so that two IDE instances running DIFFERENT extension versions don't
        // fight over the same DLLs (the running helper.exe holds file locks on
        // them). This matters during dev when an Experimental Instance loads a
        // new build while a normal instance is still using the prior one.
        // A .extracted marker per hash dir confirms a complete extraction.
        const string ResourceName = "vsagentic-mcp-permissions.zip";
        const string HelperFileName = "vsagentic-mcp-permissions.exe";
        const string MarkerFileName = ".extracted";
        var asm = typeof(ClaudeCliProcessHost).Assembly;
        var helpersRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VsAgentic", "helpers");

        byte[] zipBytes;
        using (var src = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in {asm.FullName}"))
        using (var ms = new MemoryStream())
        {
            src.CopyTo(ms);
            zipBytes = ms.ToArray();
        }

        string expectedHash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var bytes = sha.ComputeHash(zipBytes);
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            expectedHash = sb.ToString();
        }

        var hashSuffix = expectedHash.Substring(0, 16);
        var helperDir = Path.Combine(helpersRoot, hashSuffix);
        var helperPath = Path.Combine(helperDir, HelperFileName);
        var markerPath = Path.Combine(helperDir, MarkerFileName);

        if (File.Exists(markerPath) && File.Exists(helperPath))
            return helperPath;

        Directory.CreateDirectory(helperDir);
        using (var zipStream = new MemoryStream(zipBytes, writable: false))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries
                var destPath = Path.Combine(helperDir, entry.FullName);
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                using var entryStream = entry.Open();
                using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                entryStream.CopyTo(fs);
            }
        }
        File.WriteAllText(markerPath, expectedHash);
        _logger.LogInformation("[ClaudeCli] Extracted permission helper to: {Path} (hash {Hash})",
            helperPath, expectedHash.Substring(0, 12));

        return helperPath;
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
            // Wrap the raw stdin pipe in a BOM-less UTF-8 writer. On net472 the
            // ProcessStartInfo has no StandardInputEncoding property, so the default
            // StandardInput writer encodes with the system ANSI code page — which
            // mangles any non-ASCII (e.g. Cyrillic) to '?' unless the OS "Use Unicode
            // UTF-8" beta is on. Our JSON lines carry raw non-ASCII (the serializer
            // uses UnsafeRelaxedJsonEscaping), so we must force UTF-8 here.
            var stdin = new StreamWriter(_process!.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = false,
                NewLine = "\n",
            };
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

    private async Task StdoutReaderLoopAsync(Process process, Channel<JsonElement> channel, Task stderrTask, StderrTail stderrTail, CancellationToken ct)
    {
        try
        {
            var stdout = process.StandardOutput;
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

                await channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] stdout reader crashed");
        }
        finally
        {
            CompleteRun(channel, process, stderrTask, stderrTail, ct);
        }
    }

    private async Task StderrLoopAsync(Process process, StderrTail stderrTail, CancellationToken ct)
    {
        try
        {
            var stderr = process.StandardError;
            string? line;
            while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (ct.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogInformation("[ClaudeCli stderr] {Line}", line);
                    stderrTail.Add(line);
                }
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

        // The wait for the CLI to exit happens off the caller's thread, which
        // is often the UI thread. The next start waits for it instead.
        var process = _process;
        var processTree = _processTree;
        var pipeServer = _pipeServer;
        var mcpConfigPath = _mcpConfigTempPath;
        if (process != null || pipeServer != null || mcpConfigPath != null)
        {
            var previous = _shutdown;
            _shutdown = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                ShutDown(process, processTree, pipeServer, mcpConfigPath);
            });
        }

        _mcpConfigTempPath = null;
        _process = null;
        _processTree = null;
        _pipeServer = null;
        _runCts = null;
        _writerTask = null;
        _readerTask = null;
        _stderrTask = null;
        _stdinChannel = null;
        _stdoutChannel = null;
    }

    private void ShutDown(Process? process, ProcessTreeJob? processTree, PermissionPipeServer? pipeServer, string? mcpConfigPath)
    {
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    // End of input is the CLI's signal to finish: it writes out
                    // what it still holds and stops its MCP servers. Killing it
                    // at once loses that.
                    try { process.StandardInput.Close(); } catch { }
                    if (!process.WaitForExit(GracefulExitTimeoutMs))
                        _logger.LogInformation("[ClaudeCli] CLI still running {Ms} ms after its input closed; ending its process tree", GracefulExitTimeoutMs);
                }
            }
            catch { }

            // Also after a clean exit: a background command can outlive the CLI.
            if (processTree != null)
                processTree.Dispose();
            else
                try { if (!process.HasExited) process.Kill(); } catch { }

            try { process.Dispose(); } catch { }
        }

        try { pipeServer?.Dispose(); } catch { }
        try
        {
            if (!string.IsNullOrEmpty(mcpConfigPath) && File.Exists(mcpConfigPath))
                File.Delete(mcpConfigPath);
        }
        catch { }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The last lines one run wrote to stderr. When the CLI exits without a
    /// result event, they are the only account of why.
    /// </summary>
    private sealed class StderrTail
    {
        private const int MaxLines = 20;
        private readonly Queue<string> _lines = new Queue<string>();

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Enqueue(line);
                if (_lines.Count > MaxLines) _lines.Dequeue();
            }
        }

        public string Text
        {
            get { lock (_lines) return string.Join("\n", _lines); }
        }
    }

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
