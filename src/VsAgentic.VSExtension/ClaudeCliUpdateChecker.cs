using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.VSExtension.Options;

namespace VsAgentic.VSExtension;

/// <summary>
/// Compares the Claude Code CLI this extension drives against the latest release on
/// the npm registry and, when the local one is older, surfaces an InfoBar offering to
/// run <c>claude update</c>.
///
/// The CLI carries its own auto-updater, but it runs on the CLI's schedule rather than
/// ours, it is skipped entirely when the user has turned auto-updates off, and a failed
/// pass is only visible to someone who runs <c>claude doctor</c> by hand. Nothing about
/// any of that reaches a user who only ever drives the CLI through this extension, so a
/// months-old CLI stays in place unnoticed.
/// </summary>
internal sealed class ClaudeCliUpdateChecker : IVsInfoBarUIEvents
{
    // The npm "latest" dist-tag. It carries the same release as the native installer's
    // "latest" channel, so one request covers both install methods. A native install
    // pinned to the "stable" channel trails this by a few builds — see ShowInfoBar.
    private const string NpmLatestUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";
    private const string ChangelogUrl = "https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md";

    // The probe is a child process started during IDE startup, so it gets a hard
    // ceiling rather than the chance to hang the background task forever.
    private const int VersionProbeTimeoutMs = 15_000;

    private static readonly Regex s_semVer = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AsyncPackage _package;
    private readonly VsAgenticOptionsPage _options;

    // Remembered so the "Skip this version" action knows what it is skipping.
    private Version? _offered;
    private uint _eventCookie;

    public ClaudeCliUpdateChecker(AsyncPackage package, VsAgenticOptionsPage options)
    {
        _package = package;
        _options = options;
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VsAgentic", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"cliupdate-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        Log("CheckAsync: start");
        try
        {
            if (!_options.CheckForCliUpdates)
            {
                Log("CheckAsync: disabled in options");
                return;
            }

            var cliPath = string.IsNullOrWhiteSpace(_options.ClaudeCliPath) ? "claude" : _options.ClaudeCliPath.Trim();

            var installed = await ReadInstalledVersionAsync(cliPath, ct).ConfigureAwait(false);
            Log($"CheckAsync: installed = {installed?.ToString() ?? "<null>"}");

            // No CLI on the path, or a build that answers --version in a shape we do
            // not recognise. Either way there is nothing to compare against, and the
            // chat window already reports a CLI that cannot be started at all.
            if (installed is null) return;

            var latest = await FetchLatestVersionAsync(ct).ConfigureAwait(false);
            Log($"CheckAsync: npm latest = {latest?.ToString() ?? "<null>"}");
            if (latest is null) return;

            if (latest <= installed)
            {
                Log($"CheckAsync: up to date ({installed} >= {latest})");
                return;
            }

            var skipped = ReadSkippedVersion();
            if (skipped is not null && latest <= skipped)
            {
                Log($"CheckAsync: {latest} skipped by the user (skip mark {skipped})");
                return;
            }

            Log($"CheckAsync: newer CLI available, showing InfoBar ({installed} -> {latest})");
            _offered = latest;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            ShowInfoBar(installed, latest);
        }
        catch (OperationCanceledException)
        {
            Log("CheckAsync: cancelled");
        }
        catch (Exception ex)
        {
            Log($"CheckAsync: exception {ex}");
            Debug.WriteLine($"VsAgentic ClaudeCliUpdateChecker: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <c>&lt;cli&gt; --version</c> and reads the version out of its output, which
    /// looks like <c>2.1.270 (Claude Code)</c>. The CLI path comes from the same option
    /// the chat uses, so a CLI the extension can drive is a CLI this can probe.
    /// </summary>
    private static async Task<Version?> ReadInstalledVersionAsync(string cliPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Drained but not inspected: an unread stderr pipe fills up and stalls
            // the child, which would turn a cosmetic warning into a hung probe.
            _ = process.StandardError.ReadToEndAsync();

            var readTask = process.StandardOutput.ReadToEndAsync();
            var finished = await Task.WhenAny(readTask, Task.Delay(VersionProbeTimeoutMs, ct)).ConfigureAwait(false);
            if (finished != readTask)
            {
                Log("ReadInstalledVersionAsync: timed out, killing probe");
                try { process.Kill(); } catch { }
                return null;
            }

            var stdout = await readTask.ConfigureAwait(false);
            process.WaitForExit(2000);
            Log($"ReadInstalledVersionAsync: '{stdout?.Trim()}'");
            return ParseVersion(stdout);
        }
        catch (Exception ex)
        {
            Log($"ReadInstalledVersionAsync: exception {ex.Message}");
            return null;
        }
    }

    private static async Task<Version?> FetchLatestVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, NpmLatestUrl);

        // Plain JSON. The registry's abbreviated type (vnd.npm.install-v1+json) covers
        // the whole-package document only; asking for it on a single-version URL is
        // answered with 406, which cost a round of debugging to find.
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"FetchLatestVersionAsync: HTTP {(int)resp.StatusCode}");
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Only one field is needed out of the whole document, and "version" appears
        // once at its top level — dependency ranges are keyed by package name, not by
        // "version" — so a regex avoids pulling in a JSON parser, as UpdateChecker does.
        var match = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? ParseVersion(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Takes the first major.minor.patch triple out of a string. Parsed by hand rather
    /// than with Version.TryParse because a pre-release suffix such as "2.1.280-rc.1"
    /// makes that method fail outright, and the three numbers in front of it are still
    /// the comparison we want.
    /// </summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = s_semVer.Match(text);
        if (!m.Success) return null;

        return int.TryParse(m.Groups[1].Value, out var major)
               && int.TryParse(m.Groups[2].Value, out var minor)
               && int.TryParse(m.Groups[3].Value, out var patch)
            ? new Version(major, minor, patch)
            : null;
    }

    /// <summary>
    /// Path of the skip mark. Kept next to the usage log in %APPDATA%\VsAgentic rather
    /// than in the options page: it is machine state about one CLI release, not a
    /// preference anyone would want to find — or edit — in Tools → Options.
    /// </summary>
    private static string SkipMarkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VsAgentic", "cli-update-skip.txt");

    private static Version? ReadSkippedVersion()
    {
        try
        {
            var path = SkipMarkPath;
            return File.Exists(path) ? ParseVersion(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            Log($"ReadSkippedVersion: exception {ex.Message}");
            return null;
        }
    }

    private static void WriteSkippedVersion(Version version)
    {
        try
        {
            var path = SkipMarkPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, version.ToString(), new UTF8Encoding(false));
            Log($"WriteSkippedVersion: {version}");
        }
        catch (Exception ex)
        {
            // Worst case the banner comes back at the next start.
            Log($"WriteSkippedVersion: exception {ex.Message}");
        }
    }

    private void ShowInfoBar(Version installed, Version latest)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!TryGetInfoBarHost(out var host, out var factory)) return;

        // The banner names the npm latest tag. A native install pinned to the stable
        // channel gets whatever stable currently holds, which trails that tag by a few
        // builds, so such a user can update and still be told a newer one exists. The
        // skip mark is the way out of that, and it is cheaper than working the user's
        // release channel out from the install layout.
        var model = new InfoBarModel(
            textSpans: new[]
            {
                new InfoBarTextSpan($"Claude Code {latest} is available (you have {installed}). Update to install it."),
            },
            actionItems: new[]
            {
                new InfoBarHyperlink("Update", "update"),
                new InfoBarHyperlink("What's new", "changelog"),
                new InfoBarHyperlink("Skip this version", "skip"),
            },
            image: KnownMonikers.StatusInformation,
            isCloseButtonVisible: true);

        var element = factory.CreateInfoBar(model);
        element.Advise(this, out _eventCookie);
        host.AddInfoBar(element);
    }

    private static bool TryGetInfoBarHost(out IVsInfoBarHost host, out IVsInfoBarUIFactory factory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        host = null!;
        factory = null!;

        if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) return false;

        if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj))
            || hostObj is not IVsInfoBarHost h)
        {
            return false;
        }

        if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory f) return false;

        host = h;
        factory = f;
        return true;
    }

    public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_eventCookie != 0)
        {
            infoBarUIElement.Unadvise(_eventCookie);
            _eventCookie = 0;
        }
    }

    public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        switch (actionItem.ActionContext as string)
        {
            case "update":
                LaunchUpdateConsole();
                break;

            case "skip":
                if (_offered is not null) WriteSkippedVersion(_offered);
                break;

            // Reading what changed is how a user decides whether to update, so the
            // banner stays up and the choice it offers stays available. Only the two
            // actions that settle the question dismiss it.
            case "changelog":
                OpenUrl(ChangelogUrl);
                return;
        }

        infoBarUIElement.Close();
    }

    /// <summary>
    /// Opens a console window running <c>claude update</c>. The install is left to the
    /// user's own CLI rather than driven from here: it knows whether it was installed
    /// natively or through npm, and an update that rewrites the running executable
    /// has no business happening silently behind the IDE.
    /// </summary>
    private void LaunchUpdateConsole()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var cliPath = string.IsNullOrWhiteSpace(_options.ClaudeCliPath) ? "claude" : _options.ClaudeCliPath.Trim();

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",

                // The update prints what it installed and whether it worked, so /C on
                // its own would take that away the moment it finished. `& pause` keeps
                // the result on screen and then lets the window go on any key, rather
                // than leaving a dead console behind for the user to close.
                //
                // The command is wrapped in a second pair of quotes because cmd strips
                // the outer pair whenever the line holds more than two quotes or an
                // operator — both true here — which leaves the quoted path intact.
                Arguments = $"/C \"\"{cliPath}\" update & pause\"",

                // Started directly rather than through the shell, matching the login
                // console. cmd.exe is a console program and this process is not, so
                // Windows gives it a window of its own.
                UseShellExecute = false,
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"LaunchUpdateConsole: exception {ex}");
            Debug.WriteLine($"VsAgentic ClaudeCliUpdateChecker: failed to launch the update console: {ex.Message}");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VsAgentic ClaudeCliUpdateChecker: failed to open {url}: {ex.Message}");
        }
    }
}
