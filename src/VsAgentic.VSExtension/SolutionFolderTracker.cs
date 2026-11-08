using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Services;

namespace VsAgentic.VSExtension;

/// <summary>
/// Keeps the folders of the open solution's projects that lie outside the
/// solution folder. Read when a chat starts the agent, from any thread, so
/// the list is built on the UI thread ahead of time.
/// </summary>
internal sealed class SolutionFolderTracker : IWorkspaceFolders
{
    private sealed class Snapshot
    {
        public Snapshot(string root, IReadOnlyList<string> folders)
        {
            Root = root;
            Folders = folders;
        }

        public string Root { get; }
        public IReadOnlyList<string> Folders { get; }
    }

    private static readonly Snapshot Empty = new("", Array.Empty<string>());

    private readonly IVsSolution _solution;
    private readonly JoinableTaskFactory _jtf;
    private volatile Snapshot _snapshot = Empty;
    private int _refreshScheduled;

    public SolutionFolderTracker(IVsSolution solution, JoinableTaskFactory jtf)
    {
        _solution = solution;
        _jtf = jtf;
    }

    public IReadOnlyList<string> FoldersOutside(string workingDirectory)
    {
        var snapshot = _snapshot;
        return string.Equals(
            snapshot.Root.TrimEnd('\\', '/'),
            (workingDirectory ?? "").TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase)
            ? snapshot.Folders
            : Array.Empty<string>();
    }

    /// <summary>
    /// Builds the list again once Visual Studio is idle. A solution that loads
    /// its projects one by one raises an event for each; they all end in one
    /// refresh. Called from any thread.
    /// </summary>
    public void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) == 1) return;

        _ = _jtf.RunAsync(VsTaskRunContext.UIThreadIdlePriority, RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        await _jtf.SwitchToMainThreadAsync();
        Interlocked.Exchange(ref _refreshScheduled, 0);
        Refresh();
    }

    public void Clear()
    {
        _snapshot = Empty;
    }

    private void Refresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (ErrorHandler.Failed(_solution.GetSolutionInfo(out string root, out _, out _)) || string.IsNullOrEmpty(root))
            {
                _snapshot = Empty;
                return;
            }

            _snapshot = new Snapshot(root, WorkspaceFolderList.OutsideRoot(root, ProjectFolders()));
        }
        catch (Exception ex)
        {
            // The chat then works as before, in the solution folder only.
            System.Diagnostics.Debug.WriteLine($"VsAgentic: Could not list the project folders: {ex}");
        }
    }

    private List<string> ProjectFolders()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var folders = new List<string>();
        var guid = Guid.Empty;
        if (ErrorHandler.Failed(_solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref guid, out var projects))
            || projects is null)
            return folders;

        var one = new IVsHierarchy[1];
        while (projects.Next(1, one, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            // Solution folders and projects without a folder of their own
            // report nothing here, or the solution folder.
            if (ErrorHandler.Succeeded(one[0].GetProperty(
                    (uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ProjectDir, out var dir))
                && dir is string folder)
                folders.Add(folder);
        }

        return folders;
    }
}
