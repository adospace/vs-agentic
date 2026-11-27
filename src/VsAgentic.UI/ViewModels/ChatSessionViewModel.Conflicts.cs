using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Services;
using VsAgentic.UI.ViewModels.Banners;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The notice that Git stopped on conflicts in the working folder. The Git
/// folder is watched, so the notice comes when a merge, rebase, cherry-pick or
/// revert stops, and goes when the user accepts the merge or aborts it. The
/// Resolve button sends <see cref="ConflictResolutionRequest"/>; the agent
/// edits the files and stages nothing.
/// </summary>
public partial class ChatSessionViewModel
{
    // Git rewrites several files in a row; one reading after the last is enough.
    private static readonly TimeSpan ConflictReadDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The agent's name in the notice.</summary>
    private const string ConflictsAgentName = "Claude";

    // The files in the Git folder that change when conflicts come or go.
    private static readonly string[] ConflictTriggerNames =
    {
        "index", "HEAD", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "rebase-merge", "rebase-apply",
    };

    /// <summary>
    /// The conflicts notice. Shown beside <see cref="ActiveBanner"/>, not in
    /// its place: it does not wait for an answer. Null when not shown.
    /// </summary>
    [ObservableProperty]
    private ConflictBannerViewModel? _conflictBanner;

    private FileSystemWatcher? _gitWatcher;
    private Timer? _conflictReadTimer;
    private readonly CancellationTokenSource _conflictCts = new();
    private int _conflictReadVersion;

    // The conflicts the user dismissed. The notice stays away until they change.
    private string? _dismissedConflicts;

    // True from Resolve until that turn ends. The agent edits the files during
    // the turn, and a notice that counts down meanwhile is only noise.
    private bool _resolvingConflicts;

    private void InitializeConflicts()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory)) return;
        _conflictReadTimer = new Timer(_ => _ = ReadConflictsAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _ = StartConflictWatchAsync();
    }

    private async Task StartConflictWatchAsync()
    {
        try
        {
            var repo = await GitConflicts.FindRepositoryAsync(WorkingDirectory, _conflictCts.Token).ConfigureAwait(false);
            if (repo is null || _conflictCts.IsCancellationRequested) return;

            var watcher = new FileSystemWatcher(repo.Value.GitDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Changed += OnGitFolderChanged;
            watcher.Created += OnGitFolderChanged;
            watcher.Deleted += OnGitFolderChanged;
            watcher.Renamed += OnGitFolderChanged;
            watcher.Error += (_, _) => ScheduleConflictRead();
            watcher.EnableRaisingEvents = true;
            _gitWatcher = watcher;

            // The session closed while git was still answering.
            if (_conflictCts.IsCancellationRequested)
            {
                watcher.Dispose();
                return;
            }

            await ReadConflictsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Watching the Git folder of {Folder} failed", WorkingDirectory);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnGitFolderChanged(object sender, FileSystemEventArgs e)
    {
        foreach (var name in ConflictTriggerNames)
        {
            if (string.Equals(e.Name, name, StringComparison.Ordinal))
            {
                ScheduleConflictRead();
                return;
            }
        }
    }

    /// <summary>Reads the conflicts again after a short pause; later calls move the pause.</summary>
    private void ScheduleConflictRead()
    {
        try { _conflictReadTimer?.Change(ConflictReadDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadConflictsAsync()
    {
        var version = Interlocked.Increment(ref _conflictReadVersion);
        GitConflictState? state;
        try
        {
            state = await GitConflicts.ReadAsync(WorkingDirectory, _conflictCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the Git conflicts in {Folder} failed", WorkingDirectory);
            return;
        }

        Dispatch(() =>
        {
            // A later reading has started; it decides.
            if (version != Volatile.Read(ref _conflictReadVersion) || _conflictCts.IsCancellationRequested) return;
            ApplyConflicts(state);
        });
    }

    private void ApplyConflicts(GitConflictState? state)
    {
        if (state is null || state.Files.Count == 0)
        {
            ConflictBanner = null;
            // The next conflicts are new ones, even in the same files.
            if (state is null) _dismissedConflicts = null;
            return;
        }

        var signature = state.Signature;
        if (_resolvingConflicts || signature == _dismissedConflicts)
        {
            ConflictBanner = null;
            return;
        }

        // Same conflicts: keep the notice as it is, so it does not flicker.
        if (ConflictBanner?.State.Signature == signature) return;

        ConflictBannerViewModel? banner = null;
        banner = new ConflictBannerViewModel(
            state,
            ConflictsAgentName,
            canAct: () => !IsBusy,
            onResolve: () => _ = ResolveConflictsAsync(banner!),
            onMarkResolved: () => _ = MarkConflictsResolvedAsync(banner!),
            onSettleDelete: (file, keep) => _ = SettleDeleteConflictAsync(file, keep),
            onDismiss: () =>
            {
                _dismissedConflicts = banner!.State.Signature;
                if (ReferenceEquals(ConflictBanner, banner)) ConflictBanner = null;
            });
        ConflictBanner = banner;
    }

    /// <summary>
    /// Sends the request. Text already in the message box goes with it, so the
    /// user can say what to keep, and so do the attachments.
    /// </summary>
    private async Task ResolveConflictsAsync(ConflictBannerViewModel banner)
    {
        if (IsBusy) return;

        var draft = InputText.Trim();
        InputText = ConflictResolutionRequest.Build(banner.State, draft.Length == 0 ? null : draft);

        if (!SendCommand.CanExecute(null))
        {
            InputText = draft;
            return;
        }

        _resolvingConflicts = true;
        if (ReferenceEquals(ConflictBanner, banner)) ConflictBanner = null;
        await SendCommand.ExecuteAsync(null);

        // The send failed before the turn started, so no turn end will clear it.
        if (!IsBusy && _resolvingConflicts)
        {
            _resolvingConflicts = false;
            ScheduleConflictRead();
        }
    }

    /// <summary>
    /// Stages the files the user saw as edited free of conflict blocks. The
    /// click is the confirmation; the request forbids the agent to stage.
    /// </summary>
    private async Task MarkConflictsResolvedAsync(ConflictBannerViewModel banner)
    {
        var wanted = banner.State.Resolved.Select(f => f.Path).ToList();
        IReadOnlyList<string>? added;
        try
        {
            added = await GitConflicts.MarkResolvedAsync(WorkingDirectory, wanted, _conflictCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Marking {Files} as resolved failed", string.Join(", ", wanted));
            added = null;
        }

        Dispatch(() =>
        {
            banner.IsWorking = false;
            if (added is null)
                AddNotice("_Could not mark the files as resolved: `git add` failed._", "conflicts");
            else if (added.Count < wanted.Count)
                AddNotice($"_Marked {added.Count} of {wanted.Count} files as resolved. The others changed and hold conflict markers again, or are no longer unmerged._", "conflicts");
            ScheduleConflictRead();
        });
    }

    /// <summary>Keeps or deletes a file only one side has. The click is the confirmation.</summary>
    private async Task SettleDeleteConflictAsync(GitConflictFile file, bool keep)
    {
        bool done;
        try
        {
            done = await GitConflicts.SettleDeleteConflictAsync(WorkingDirectory, file.Path, keep, _conflictCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settling the delete conflict in {File} failed", file.Path);
            done = false;
        }

        Dispatch(() =>
        {
            if (!done)
            {
                var action = keep ? "keep" : "delete";
                AddNotice($"_Could not {action} `{file.Path}`: it changed since, or `git {(keep ? "add" : "rm")}` failed._", "conflicts");
            }
            // The new reading replaces the notice, and with it the item that is still working.
            ConflictBanner = null;
            ScheduleConflictRead();
        });
    }

    /// <summary>Called when <see cref="IsBusy"/> changes.</summary>
    private void OnBusyChangedForConflicts(bool busy)
    {
        ConflictBanner?.RefreshCanAct();
        if (busy) return;

        // The files may have changed in the turn, whoever started it.
        _resolvingConflicts = false;
        ScheduleConflictRead();
    }

    /// <summary>Called when the user comes back to the session; they may have edited a file meanwhile.</summary>
    private void OnFocusedForConflicts() => ScheduleConflictRead();

    private void DisposeConflicts()
    {
        try { _conflictCts.Cancel(); } catch { }
        try { _gitWatcher?.Dispose(); } catch { }
        try { _conflictReadTimer?.Dispose(); } catch { }
        _gitWatcher = null;
        _conflictReadTimer = null;
    }
}
