using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.Services;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// Says that Git stopped on conflicts in the working folder. While files hold
/// conflict blocks it offers to have the agent resolve them; once files are
/// edited free of blocks it offers to mark them as resolved, which is the only
/// step that stages anything. Not a prompt: the chat goes on while it is
/// shown, so it has its own place above the input box and does not take
/// <c>ActiveBanner</c>.
/// </summary>
public partial class ConflictBannerViewModel : ObservableObject
{
    private const int ListedFiles = 3;

    private readonly Action _onResolve;
    private readonly Action _onMarkResolved;
    private readonly Action _onDismiss;
    private readonly Func<bool> _canAct;

    public ConflictBannerViewModel(
        GitConflictState state,
        string agentName,
        Func<bool> canAct,
        Action onResolve,
        Action onMarkResolved,
        Action<GitConflictFile, bool> onSettleDelete,
        Action onDismiss)
    {
        _canAct = canAct;
        _onResolve = onResolve;
        _onMarkResolved = onMarkResolved;
        _onDismiss = onDismiss;
        State = state;

        var open = state.Open;
        var resolved = state.Resolved;
        ShowResolve = open.Count > 0;
        ShowMarkResolved = resolved.Count > 0;

        var prefix = state.Operation switch
        {
            GitConflictOperation.Merge => "Merge: ",
            GitConflictOperation.Rebase => "Rebase: ",
            GitConflictOperation.CherryPick => "Cherry-pick: ",
            GitConflictOperation.Revert => "Revert: ",
            _ => "",
        };

        if (open.Count > 0)
        {
            Title = prefix + Count(open.Count, "file has conflicts", "files have conflicts");
            DetailMessage = Names(open);
            if (resolved.Count > 0)
                DetailMessage += "\nEdited, not yet marked as resolved: " + Names(resolved);
            SubText = $"{agentName} edits the files and explains each choice. Nothing is staged until you mark the files as resolved.";
        }
        else
        {
            Title = prefix + Count(resolved.Count, "file edited, not yet marked as resolved", "files edited, not yet marked as resolved");
            DetailMessage = Names(resolved);
            SubText = "Check the changes (Diff in the chat), then mark the files as resolved. That stages them; it does not commit.";
        }

        // The merge editor before Visual Studio 18.12 builds its result from
        // the conflict, not from the file, and accepting it replaces the edits.
        if (resolved.Count > 0)
            Warning = "Do not accept these files in the merge editor of Visual Studio 18.11 or earlier: it shows the conflict again, and Accept Merge replaces the edits.";

        // Visual Studio 18.11 offers no side to pick for these, so the notice does.
        DeleteConflicts = open
            .Where(f => f.IsKeepOrDeleteConflict)
            .Select(f => new DeleteConflictItem(f, canAct, onSettleDelete))
            .ToList();

        ResolveLabel = $"Resolve with {agentName}";
        MarkResolvedLabel = resolved.Count == 1 ? "Mark as resolved" : $"Mark {resolved.Count} as resolved";
    }

    public GitConflictState State { get; }
    public string Title { get; }
    public string DetailMessage { get; }
    public string SubText { get; }
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;
    public string ResolveLabel { get; }
    public string MarkResolvedLabel { get; }
    public bool ShowResolve { get; }
    public bool ShowMarkResolved { get; }
    /// <summary>The files only one side has, each with Keep and Delete.</summary>
    public IReadOnlyList<DeleteConflictItem> DeleteConflicts { get; }
    public bool HasDeleteConflicts => DeleteConflicts.Count > 0;
    public bool MarkResolvedIsMain => ShowMarkResolved && !ShowResolve;
    public bool MarkResolvedIsSecond => ShowMarkResolved && ShowResolve;

    /// <summary>True while the files are being marked as resolved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkResolvedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveCommand))]
    private bool _isWorking;

    /// <summary>Tells the buttons to ask again, for example when a turn ends.</summary>
    public void RefreshCanAct()
    {
        ResolveCommand.NotifyCanExecuteChanged();
        MarkResolvedCommand.NotifyCanExecuteChanged();
        foreach (var item in DeleteConflicts) item.RefreshCanAct();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Resolve() => _onResolve();

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void MarkResolved()
    {
        IsWorking = true;
        _onMarkResolved();
    }

    private bool CanAct() => !IsWorking && _canAct();

    [RelayCommand]
    private void Dismiss() => _onDismiss();

    /// <summary>One file that only one side has: deleted, or renamed away, on the other.</summary>
    public partial class DeleteConflictItem : ObservableObject
    {
        private readonly GitConflictFile _file;
        private readonly Func<bool> _canAct;
        private readonly Action<GitConflictFile, bool> _onSettle;

        public DeleteConflictItem(GitConflictFile file, Func<bool> canAct, Action<GitConflictFile, bool> onSettle)
        {
            _file = file;
            _canAct = canAct;
            _onSettle = onSettle;
            FileName = System.IO.Path.GetFileName(file.Path);
            // In the words of Git Changes: current is the checked-out branch, incoming the other side.
            Description = file.Code switch
            {
                "DU" => "deleted in current, changed in incoming",
                "UD" => "changed in current, deleted in incoming",
                "DD" => "deleted in both",
                // Rename/delete and rename/rename: Git writes no markers.
                "AU" => "only in current; incoming renamed or deleted it",
                "UA" => "only in incoming; current renamed or deleted it",
                _ => file.Kind,
            };
            CanKeep = file.CanKeep;
        }

        public string FileName { get; }
        public string Description { get; }
        public bool CanKeep { get; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(KeepCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
        private bool _isWorking;

        public void RefreshCanAct()
        {
            KeepCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanKeepNow))]
        private void Keep()
        {
            IsWorking = true;
            _onSettle(_file, true);
        }

        [RelayCommand(CanExecute = nameof(CanAct))]
        private void Delete()
        {
            IsWorking = true;
            _onSettle(_file, false);
        }

        private bool CanAct() => !IsWorking && _canAct();
        private bool CanKeepNow() => CanKeep && CanAct();
    }

    private static string Count(int n, string one, string many) => n == 1 ? "1 " + one : $"{n} " + many;

    private static string Names(IReadOnlyList<GitConflictFile> files)
    {
        var names = files.Take(ListedFiles).Select(f => System.IO.Path.GetFileName(f.Path));
        return string.Join(", ", names) + (files.Count > ListedFiles ? $" and {files.Count - ListedFiles} more" : "");
    }
}
