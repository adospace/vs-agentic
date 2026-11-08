using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The part of the setup check that is about Claude Code: the CLI and its
/// version, sign-in, VsAgentic's permission server, and the settings files
/// the CLI reads. Every check only reads; none of them starts a session or
/// spends tokens.
///
/// CLI 2.1.316 has a <c>/doctor</c> of its own, but in a stream-json session
/// it is a skill: the model runs shell commands to inspect the install,
/// writes helper scripts to disk and is billed for it (measured 8. 11.: three
/// turns, 0.29 USD). The interactive <c>claude doctor</c> is listed under
/// <c>terminal_slash_commands</c> and does not run here. Hence our own.
/// </summary>
public static class ClaudeSetupCheck
{
    /// <summary>
    /// The oldest CLI this version of VsAgentic was tested against. An older
    /// one still starts, but features that need newer protocol may be missing.
    /// </summary>
    public static readonly Version MinimumTestedCliVersion = new Version(2, 1, 296);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex SemVer = new Regex(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>The CLI, its version and sign-in.</summary>
    public static async Task<IReadOnlyList<SetupCheckItem>> CheckCliAsync(string? cliPathSetting, CancellationToken ct)
    {
        var items = new List<SetupCheckItem>();
        var cliPath = string.IsNullOrWhiteSpace(cliPathSetting) ? "claude" : cliPathSetting!.Trim();
        var resolved = ResolveExecutable(cliPath);

        var version = await RunAsync(cliPath, "--version", ct).ConfigureAwait(false);
        if (version.StartError is not null)
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Error, "Claude Code CLI",
                NotFoundDetail(cliPath, version.StartError)));
            return items;
        }

        var parsed = ParseVersion(version.Stdout);
        var where = resolved is null ? "" : $" (`{resolved}`)";
        if (parsed is null)
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Warning, "Claude Code CLI",
                $"Started{where}, but `--version` did not answer with a version number" +
                (version.TimedOut ? " in 15 seconds." : $": {Quote(version.Stdout + version.Stderr)}.") +
                " Check that Claude CLI Path in Tools → Options → VsAgentic points to Claude Code."));
            return items;
        }

        items.Add(parsed < MinimumTestedCliVersion
            ? new SetupCheckItem(SetupCheckStatus.Warning, "Claude Code CLI",
                $"Version {parsed}{where} is older than {MinimumTestedCliVersion}, the oldest this VsAgentic was tested with. " +
                "Run `claude update` in a terminal.")
            : new SetupCheckItem(SetupCheckStatus.Ok, "Claude Code CLI", $"Version {parsed}{where}."));

        items.Add(await CheckSignInAsync(cliPath, ct).ConfigureAwait(false));
        return items;
    }

    private static string NotFoundDetail(string cliPath, string error)
    {
        var setting = cliPath == "claude" ? "" : $" Claude CLI Path is set to `{cliPath}`.";
        return "Not found or could not start (" + error + ")." + setting +
               " Install Claude Code (https://code.claude.com/docs/en/setup), or set Tools → Options → VsAgentic → " +
               "Claude CLI Path to the full path of `claude.exe`. Restart Visual Studio after installing, so it sees the new PATH.";
    }

    /// <summary>
    /// <c>claude auth status</c> prints JSON and needs no session. The API key
    /// is cleared as for the chat, so the answer is the one the chat gets.
    /// The e-mail address and organisation are left out of the report: it is
    /// meant to be pasted into a public issue.
    /// </summary>
    private static async Task<SetupCheckItem> CheckSignInAsync(string cliPath, CancellationToken ct)
    {
        const string title = "Sign-in";
        var result = await RunAsync(cliPath, "auth status --json", ct).ConfigureAwait(false);

        JsonElement root;
        try
        {
            var json = result.Stdout.Trim();
            var start = json.IndexOf('{');
            if (start < 0) throw new JsonException("no JSON");
            using var doc = JsonDocument.Parse(json.Substring(start));
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new SetupCheckItem(SetupCheckStatus.Warning, title,
                "Could not check: this CLI did not answer `claude auth status`. Run `claude update` in a terminal.");
        }

        var loggedIn = root.TryGetProperty("loggedIn", out var li) && li.ValueKind == JsonValueKind.True;
        if (!loggedIn)
        {
            return new SetupCheckItem(SetupCheckStatus.Error, title,
                "Not signed in. Run `claude` in a terminal and type `/login`, or send a message in a chat and click Sign in on the banner.");
        }

        var method = CliProcessInfo.ReadString(root, "authMethod");
        var plan = CliProcessInfo.ReadString(root, "subscriptionType");
        var provider = CliProcessInfo.ReadString(root, "apiProvider");

        var sb = new StringBuilder("Signed in");
        if (!string.IsNullOrEmpty(method)) sb.Append(" with ").Append(method);
        if (!string.IsNullOrEmpty(plan)) sb.Append(" (").Append(Capitalize(plan!)).Append(" plan)");
        if (!string.IsNullOrEmpty(provider) && provider != "firstParty") sb.Append(", provider ").Append(provider);
        sb.Append('.');
        return new SetupCheckItem(SetupCheckStatus.Ok, title, sb.ToString());
    }

    /// <summary>
    /// Starts VsAgentic's MCP helper the way the CLI does, with a pipe of our
    /// own, and sends it the MCP <c>initialize</c> request. Covers the three
    /// ways it has failed before: not extracted, not loading its assemblies,
    /// and not reaching back to Visual Studio over the pipe.
    /// </summary>
    public static async Task<SetupCheckItem> CheckPermissionServerAsync(ILogger? logger, CancellationToken ct)
    {
        const string title = "Permission server";
        var logFile = Path.Combine(ClaudeCliProcessHost.HelperLogDirectory,
            "vsagentic-mcp-helper-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        var logHint = $" Its log is `{logFile}`.";

        string helperPath;
        try
        {
            helperPath = ClaudeCliProcessHost.ResolveHelperExePath(logger);
        }
        catch (Exception ex)
        {
            return new SetupCheckItem(SetupCheckStatus.Error, title,
                $"Could not be unpacked: {Quote(ex.Message)}. Tools that need your approval will not run. " +
                "Check that `%LOCALAPPDATA%\\VsAgentic\\helpers` is writable and not blocked by antivirus.");
        }

        var pipeName = $"vsagentic-check-{Guid.NewGuid():N}";
        var secret = Guid.NewGuid().ToString("N");
        var watch = Stopwatch.StartNew();

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = Task.Factory.FromAsync(server.BeginWaitForConnection, server.EndWaitForConnection, null);
        // A helper that never connects leaves this to fail when the pipe is disposed.
        _ = connected.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = helperPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["VSAGENTIC_PERMISSION_PIPE"] = pipeName;
            psi.EnvironmentVariables["VSAGENTIC_PERMISSION_SECRET"] = secret;
            psi.EnvironmentVariables["VSAGENTIC_PERMISSION_LOG_DIR"] = ClaudeCliProcessHost.HelperLogDirectory;

            try
            {
                Directory.CreateDirectory(ClaudeCliProcessHost.HelperLogDirectory);
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                return new SetupCheckItem(SetupCheckStatus.Error, title,
                    $"`{helperPath}` did not start: {Quote(ex.Message)}. Antivirus may be blocking it.");
            }
            if (process is null)
                return new SetupCheckItem(SetupCheckStatus.Error, title, $"`{helperPath}` did not start.");

            var stderr = process.StandardError.ReadToEndAsync();

            if (await Task.WhenAny(connected, Task.Delay(HelperTimeout, ct)).ConfigureAwait(false) != connected)
            {
                var exited = process.HasExited ? $" It exited with code {process.ExitCode}." : "";
                return new SetupCheckItem(SetupCheckStatus.Error, title,
                    $"Started, but did not connect back to Visual Studio in {HelperTimeout.TotalSeconds:0} seconds.{exited}{logHint}");
            }
            await connected.ConfigureAwait(false);

            using var pipeReader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var hello = await WithTimeout(pipeReader.ReadLineAsync(), ct).ConfigureAwait(false);
            if (hello != secret)
            {
                return new SetupCheckItem(SetupCheckStatus.Error, title,
                    "Connected to Visual Studio but did not send the expected handshake." + logHint);
            }

            var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" };
            await stdin.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"," +
                "\"capabilities\":{},\"clientInfo\":{\"name\":\"vsagentic-setup-check\",\"version\":\"1\"}}}").ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);

            var answered = false;
            while (true)
            {
                var line = await WithTimeout(process.StandardOutput.ReadLineAsync(), ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.IndexOf("\"id\":1", StringComparison.Ordinal) >= 0)
                {
                    answered = line.IndexOf("\"result\"", StringComparison.Ordinal) >= 0;
                    break;
                }
            }

            // Closing stdin is how the CLI ends it, too.
            try { stdin.Dispose(); } catch { }

            if (!answered)
            {
                var err = process.WaitForExit(2000) ? await stderr.ConfigureAwait(false) : "";
                return new SetupCheckItem(SetupCheckStatus.Error, title,
                    "Started and connected, but did not answer the MCP handshake" +
                    (string.IsNullOrWhiteSpace(err) ? "." : $": {Quote(err)}.") + logHint);
            }

            return new SetupCheckItem(SetupCheckStatus.Ok, title,
                $"Starts and answers ({watch.ElapsedMilliseconds} ms).");
        }
        catch (TimeoutException)
        {
            return new SetupCheckItem(SetupCheckStatus.Error, title,
                $"Did not answer in {HelperTimeout.TotalSeconds:0} seconds." + logHint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SetupCheckItem(SetupCheckStatus.Error, title, $"Check failed: {Quote(ex.Message)}." + logHint);
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.WaitForExit(2000)) process.Kill(); } catch { }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The settings files the CLI reads for this folder. A file that is not
    /// valid JSON is ignored by the CLI as a whole, with every rule in it.
    /// Comments and trailing commas are accepted, as the CLI accepts them.
    /// </summary>
    public static IReadOnlyList<SetupCheckItem> CheckSettingsFiles(string? workingDirectory)
    {
        var items = new List<SetupCheckItem>();
        var read = new List<string>();

        foreach (var path in SettingsFiles(workingDirectory))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("the file is not a JSON object");
                read.Add(path);
            }
            catch (JsonException ex)
            {
                var where = ex.LineNumber is { } line ? $" at line {line + 1}" : "";
                items.Add(new SetupCheckItem(SetupCheckStatus.Error, "Settings file",
                    $"`{path}` is not valid JSON{where}, so Claude Code ignores all of it, permission rules included. Fix the file and start a new chat."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                items.Add(new SetupCheckItem(SetupCheckStatus.Warning, "Settings file",
                    $"`{path}` could not be read: {Quote(ex.Message)}."));
            }
        }

        if (items.Count == 0)
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Ok, "Settings files", read.Count == 0
                ? "None found; Claude Code uses its defaults."
                : string.Join(", ", read.Select(p => $"`{p}`")) + (read.Count == 1 ? " reads" : " read") + " without errors."));
        }
        return items;
    }

    private static IEnumerable<string> SettingsFiles(string? workingDirectory)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            yield return Path.Combine(programData, "ClaudeCode", "managed-settings.json");

        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var userDir = string.IsNullOrEmpty(configDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configDir!;
        yield return Path.Combine(userDir, "settings.json");

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            yield return Path.Combine(workingDirectory!, ".claude", "settings.json");
            yield return Path.Combine(workingDirectory!, ".claude", "settings.local.json");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private sealed class RunResult
    {
        public string Stdout = "";
        public string Stderr = "";
        public string? StartError;
        public bool TimedOut;
    }

    /// <summary>
    /// Runs the CLI the way the chat starts it: the configured path as the
    /// file name, no shell, the API key cleared.
    /// </summary>
    private static async Task<RunResult> RunAsync(string cliPath, string arguments, CancellationToken ct)
    {
        var result = new RunResult();
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ClaudeCliChatService.UseSubscriptionAuth(psi);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex)
        {
            result.StartError = ex.Message.TrimEnd('.', ' ');
            return result;
        }

        using (process)
        {
            try { process.StandardInput.Close(); } catch { }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var both = Task.WhenAll(stdout, stderr);

            if (await Task.WhenAny(both, Task.Delay(ProbeTimeout, ct)).ConfigureAwait(false) != both)
            {
                result.TimedOut = true;
                try { process.Kill(); } catch { }
                ct.ThrowIfCancellationRequested();
                return result;
            }

            result.Stdout = await stdout.ConfigureAwait(false);
            result.Stderr = await stderr.ConfigureAwait(false);
            process.WaitForExit(2000);
        }
        return result;
    }

    /// <summary>
    /// Where Windows finds <paramref name="cliPath"/> when it is started
    /// without a shell: as given when it has a folder, otherwise on PATH, with
    /// <c>.exe</c> added when it has no extension. Null when not found.
    /// </summary>
    internal static string? ResolveExecutable(string cliPath)
    {
        try
        {
            var withExe = Path.HasExtension(cliPath) ? cliPath : cliPath + ".exe";
            if (Path.IsPathRooted(cliPath) || cliPath.IndexOfAny(new[] { '\\', '/' }) >= 0)
                return File.Exists(withExe) ? Path.GetFullPath(withExe) : File.Exists(cliPath) ? Path.GetFullPath(cliPath) : null;

            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), withExe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (ArgumentException) { }
        return null;
    }

    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SemVer.Match(text);
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, CancellationToken ct)
    {
        if (await Task.WhenAny(task, Task.Delay(HelperTimeout, ct)).ConfigureAwait(false) != task)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        return await task.ConfigureAwait(false);
    }

    /// <summary>A short, one-line excerpt of a message, for inside a sentence.</summary>
    private static string Quote(string? text)
    {
        var line = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (line.Length > 200) line = line.Substring(0, 200) + "…";
        return line.Length == 0 ? "(no output)" : "\"" + line.Replace("`", "'") + "\"";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
