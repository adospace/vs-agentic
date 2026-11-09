using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using Task = System.Threading.Tasks.Task;

namespace VsAgentic.VSExtension;

/// <summary>
/// Runs a fix the setup check offers, from the chat or from the dialog in
/// Tools → Options. Commands run in a terminal window the user sees
/// (<see cref="ClaudeSetupFixer"/>); a new path to <c>claude.exe</c> goes into
/// Tools → Options and into every open chat, so no restart is needed.
///
/// Returns one sentence on what happened, for the top of the next report.
/// The report that follows says whether the fix worked.
/// </summary>
internal static class SetupFixHost
{
    public static async System.Threading.Tasks.Task<string?> ApplyAsync(
        VsAgenticPackage package, SetupFix fix, CancellationToken ct)
    {
        switch (fix.Kind)
        {
            case SetupFixKind.UseFoundCli:
                if (string.IsNullOrEmpty(fix.Path)) return null;
                await SetCliPathAsync(package, fix.Path!, ct);
                return $"Claude CLI Path is now `{fix.Path}`.";

            case SetupFixKind.InstallCli:
            {
                var code = await ClaudeSetupFixer.InstallCliAsync(ct).ConfigureAwait(false);
                if (code != 0)
                    return $"The installer failed with exit code {code}. Its window showed why.";

                var found = ClaudeSetupFixer.FindInstalledCli().FirstOrDefault();
                if (found is null)
                    return "The installer finished, but VsAgentic did not find `claude.exe`. " +
                           "Set Claude CLI Path in Tools → Options → VsAgentic to its full path.";

                // Visual Studio still has the PATH it started with, which may
                // not have the folder the installer put on it.
                await SetCliPathAsync(package, found, ct);
                return $"Claude Code is installed. Claude CLI Path is now `{found}`.";
            }

            case SetupFixKind.UpdateCli:
            {
                var cli = fix.Path ?? await CurrentCliPathAsync(package, ct);
                var code = await ClaudeSetupFixer.UpdateCliAsync(cli, ct).ConfigureAwait(false);
                return code == 0 ? "`claude update` finished." : $"`claude update` failed with exit code {code}.";
            }

            case SetupFixKind.SignIn:
            {
                var cli = fix.Path ?? await CurrentCliPathAsync(package, ct);
                var code = await ClaudeSetupFixer.SignInAsync(cli, ct).ConfigureAwait(false);
                return code == 0 ? "The sign-in window closed." : $"Sign-in ended with exit code {code}.";
            }

            case SetupFixKind.InstallWebView2:
                if (!string.IsNullOrEmpty(fix.Path))
                    Process.Start(new ProcessStartInfo(fix.Path!) { UseShellExecute = true })?.Dispose();
                return "The download page opened in your browser. Restart Visual Studio after installing the runtime.";
        }
        return null;
    }

    private static async System.Threading.Tasks.Task<string> CurrentCliPathAsync(VsAgenticPackage package, CancellationToken ct)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var path = package.OptionsPage?.ClaudeCliPath;
        return string.IsNullOrWhiteSpace(path) ? "claude" : path!.Trim();
    }

    /// <summary>Saves the path in Tools → Options and gives it to open chats.</summary>
    private static async Task SetCliPathAsync(VsAgenticPackage package, string path, CancellationToken ct)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var page = package.OptionsPage;
        var old = page?.ClaudeCliPath;
        if (page is not null)
        {
            page.ClaudeCliPath = path;
            page.SaveSettingsToStorage();
        }
        package.UpdateOpenChatsCliPath(old, path);
    }
}
