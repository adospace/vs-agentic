using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Diagnostics;
using VsAgentic.UI.Controls;
using Task = System.Threading.Tasks.Task;

namespace VsAgentic.VSExtension;

/// <summary>
/// Gathers what the setup check needs from Visual Studio — the options as the
/// next chat will read them, the solution folder, the WebView2 runtime and
/// the versions for the report's footer — and runs it off the UI thread.
/// Used by <c>/doctor</c> in a chat and by the button in Tools → Options.
/// </summary>
internal static class SetupCheckHost
{
    public static async System.Threading.Tasks.Task<SetupCheckReport> RunAsync(
        VsAgenticPackage package,
        string? workingDirectory,
        ISessionSetupInfo? session,
        ILogger? logger,
        CancellationToken ct)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // Read here rather than from the chat's own copy: a path fixed in
        // Options after the chat opened is the one to check.
        var cliPath = package.OptionsPage?.ClaudeCliPath;
        var environment = DescribeEnvironment();

        return await Task.Run(() => SetupChecker.RunAsync(new SetupCheckRequest
        {
            ClaudeCliPath = cliPath,
            WorkingDirectory = workingDirectory,
            Environment = environment,
            HostItems = new[] { ChatWebView.CheckRuntime() },
            Session = session,
            Logger = logger,
        }, ct), ct).ConfigureAwait(false);
    }

    private static string DescribeEnvironment()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var parts = new List<string>();
        var own = UpdateChecker.ReadRunningVersion();
        parts.Add("VsAgentic " + (own?.ToString() ?? "(version unknown)"));

        if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell
            && shell.GetProperty((int)__VSSPROPID5.VSSPROPID_ReleaseVersion, out var release) == 0
            && release is string vs && vs.Length > 0)
        {
            // "18.0.11205.157 D18.0": the build number is what matters.
            parts.Add("Visual Studio " + vs.Split(' ')[0]);
        }

        parts.Add("Windows " + Environment.OSVersion.Version);
        return string.Join(" · ", parts);
    }
}
