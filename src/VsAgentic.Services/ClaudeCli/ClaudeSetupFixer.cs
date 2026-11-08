using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The fixes the setup check offers for Claude Code: install it, update it,
/// sign in, and find a <c>claude.exe</c> that Visual Studio does not see.
///
/// Every command runs in a terminal window the user can see and read, never
/// hidden: the installer is a script from the web, and sign-in needs the
/// browser and the keyboard. VsAgentic waits for the window to close and then
/// checks again.
/// </summary>
public static class ClaudeSetupFixer
{
    /// <summary>Anthropic's installer for Windows, as on code.claude.com.</summary>
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>
    /// Where Claude Code puts <c>claude.exe</c>, most likely first, followed by
    /// what the current PATH in the registry finds. Only paths that exist.
    ///
    /// Visual Studio keeps the PATH it started with, so a CLI installed while
    /// it runs is on the new PATH in the registry but not on Visual Studio's.
    /// </summary>
    public static IReadOnlyList<string> FindInstalledCli()
    {
        var found = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                var full = Path.GetFullPath(path);
                if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // The native installer, which `claude install` and install.ps1 use.
        if (!string.IsNullOrEmpty(profile)) Add(Path.Combine(profile, ".local", "bin", "claude.exe"));

        foreach (var dir in RegistryPath())
        {
            Add(SafeCombine(dir, "claude.exe"));
            Add(SafeCombine(dir, "claude.cmd"));
        }

        // winget and npm, should the user have installed it that way.
        if (!string.IsNullOrEmpty(local)) Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe"));
        if (!string.IsNullOrEmpty(roaming)) Add(Path.Combine(roaming, "npm", "claude.cmd"));

        return found;
    }

    /// <summary>
    /// The PATH a new terminal would get: the machine's, then the user's,
    /// with variables expanded. Empty when the registry cannot be read.
    /// </summary>
    private static IEnumerable<string> RegistryPath()
    {
        var parts = new List<string>();
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            if (machine?.GetValue("Path") is string m) parts.AddRange(m.Split(';'));
            using var user = Registry.CurrentUser.OpenSubKey("Environment");
            if (user?.GetValue("Path") is string u) parts.AddRange(u.Split(';'));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

        return parts
            .Select(p => Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')))
            .Where(p => p.Length > 0);
    }

    private static string? SafeCombine(string dir, string file)
    {
        try { return Path.Combine(dir, file); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Runs Anthropic's installer in a terminal window and waits for the
    /// window to close. Returns the installer's exit code.
    /// </summary>
    public static Task<int> InstallCliAsync(CancellationToken ct) => RunInTerminalAsync(
        "Installing Claude Code with Anthropic's installer",
        // A child PowerShell: the installer ends with `exit` when it fails,
        // which would close this window before the user could read why.
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command '" + InstallCommand + "'",
        ct);

    /// <summary>Runs <c>claude update</c> in a terminal window.</summary>
    public static Task<int> UpdateCliAsync(string cliPath, CancellationToken ct) => RunInTerminalAsync(
        "Updating Claude Code",
        "& " + PsQuote(cliPath) + " update",
        ct);

    /// <summary>
    /// Runs <c>claude auth login</c> in a terminal window. The API key is
    /// cleared, as for the chat, so the sign-in is the one the chat uses.
    /// </summary>
    public static Task<int> SignInAsync(string cliPath, CancellationToken ct) => RunInTerminalAsync(
        "Signing in to Claude Code. Follow the steps in the browser that opens",
        "& " + PsQuote(cliPath) + " auth login",
        ct);

    /// <summary>
    /// Shows <paramref name="what"/> and the command, runs it, and keeps the
    /// window open until the user presses Enter, so the outcome can be read.
    /// </summary>
    private static async Task<int> RunInTerminalAsync(string what, string command, CancellationToken ct)
    {
        var script = new StringBuilder()
            .AppendLine("$Host.UI.RawUI.WindowTitle = 'VsAgentic: " + what.Replace("'", "''") + "'")
            .AppendLine("Write-Host " + PsQuote("VsAgentic · " + what + "."))
            .AppendLine("Write-Host " + PsQuote("> " + command) + " -ForegroundColor DarkGray")
            .AppendLine("Write-Host ''")
            .AppendLine(command)
            .AppendLine("$code = $LASTEXITCODE; if ($null -eq $code) { $code = 0 }")
            .AppendLine("Write-Host ''")
            .AppendLine("if ($code -eq 0) { Write-Host 'Done. VsAgentic checks the setup again when this window closes.' -ForegroundColor Green }")
            .AppendLine("else { Write-Host \"Failed with exit code $code. VsAgentic checks the setup again when this window closes.\" -ForegroundColor Yellow }")
            .AppendLine("Read-Host 'Press Enter to close this window' | Out-Null")
            .AppendLine("exit $code")
            .ToString();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            // A console program started from Visual Studio gets a window of
            // its own. UseShellExecute is off so the key can be cleared.
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        ClaudeCliChatService.UseSubscriptionAuth(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();

        using (ct.Register(() => exited.TrySetCanceled()))
            await exited.Task.ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <summary>A PowerShell string literal.</summary>
    private static string PsQuote(string text) => "'" + text.Replace("'", "''") + "'";
}
