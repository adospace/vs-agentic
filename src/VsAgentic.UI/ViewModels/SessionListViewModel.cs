using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Models;

namespace VsAgentic.UI.ViewModels;

public partial class SessionInfo : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The persisted session ID from the store. Null for sessions not yet saved.
    /// </summary>
    public int? PersistedId { get; set; }

    [ObservableProperty]
    private string _name = "New Session";

    [ObservableProperty]
    private DateTime _lastActivity = DateTime.Now;

    /// <summary>
    /// Friendly relative representation of <see cref="LastActivity"/>
    /// (e.g. "today at 12:45", "yesterday", "3 days ago", "2 months ago").
    /// </summary>
    public string LastActivityDisplay => FormatLastActivity(LastActivity);

    private static string FormatLastActivity(DateTime when)
    {
        var now = DateTime.Now;
        var days = (now.Date - when.Date).Days;

        if (days <= 0)
            return $"today at {when.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}";
        if (days == 1)
            return "yesterday";
        if (days < 7)
            return $"{days} days ago";
        if (days < 14)
            return "1 week ago";
        if (days < 30)
            return $"{days / 7} weeks ago";
        if (days < 60)
            return "1 month ago";
        if (days < 365)
            return $"{days / 30} months ago";
        if (days < 730)
            return "1 year ago";
        return $"{days / 365} years ago";
    }

    partial void OnLastActivityChanged(DateTime value)
        => OnPropertyChanged(nameof(LastActivityDisplay));

    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// Pinned sessions stay at the top of the list.
    /// </summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>
    /// True on the first unpinned row when pinned rows sit above it in the
    /// visible list. The row draws a separator there, so the two groups read
    /// apart. Set by <see cref="SessionListViewModel"/>.
    /// </summary>
    [ObservableProperty]
    private bool _startsUnpinnedGroup;

    /// <summary>
    /// True while the row shows its inline rename box. Only one session at a
    /// time is in this state.
    /// </summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>
    /// Scratch buffer for the rename box, so cancelling leaves
    /// <see cref="Name"/> untouched.
    /// </summary>
    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>
    /// True once the user renamed this session by hand; mirrors
    /// <see cref="SessionEntry.TitleIsCustom"/>.
    /// </summary>
    public bool HasCustomTitle { get; set; }

    /// <summary>
    /// Cumulative USD cost for this session. Null until the first message is sent.
    /// </summary>
    [ObservableProperty]
    private decimal? _sessionCost;

    /// <summary>
    /// Formatted cost string shown in the session list (e.g. "$0.0042").
    /// Empty string when cost is not yet available.
    /// </summary>
    public string SessionCostDisplay => SessionCost.HasValue
        ? $"${SessionCost.Value:F2}"
        : string.Empty;

    partial void OnSessionCostChanged(decimal? value)
        => OnPropertyChanged(nameof(SessionCostDisplay));

    /// <summary>
    /// What the open session is doing right now. Copied from its
    /// <see cref="ChatSessionViewModel"/>; Idle for a session with no window.
    /// </summary>
    [ObservableProperty]
    private SessionActivity _activity;

    /// <summary>
    /// The line the row shows instead of "Last activity" while the session
    /// works, waits or runs background tasks. While a turn runs it is the same
    /// text as the bar under the chat.
    /// </summary>
    [ObservableProperty]
    private string _activityStatus = string.Empty;

    /// <summary>
    /// Commands started with run_in_background still run after the turn.
    /// </summary>
    [ObservableProperty]
    private bool _hasBackgroundTasks;
}

public partial class SessionListViewModel : ObservableObject
{
    private ISessionStore? _sessionStore;
    private string? _folderPath;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    public ICollectionView FilteredSessions { get; }

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public event Action<SessionInfo>? SessionOpenRequested;
    public event Action<SessionInfo>? SessionRemoved;

    /// <summary>
    /// Asked before a session is deleted; returns true to go ahead. The host
    /// supplies it, because deleting a session is not reversible and the shell
    /// owns the dialog. Left null the delete proceeds unasked, which keeps the
    /// view model usable outside Visual Studio.
    /// </summary>
    public Func<SessionInfo, bool>? ConfirmDelete { get; set; }

    public SessionListViewModel()
    {
        FilteredSessions = CollectionViewSource.GetDefaultView(Sessions);
        FilteredSessions.Filter = obj =>
            obj is SessionInfo s
            && (string.IsNullOrWhiteSpace(SearchText)
                || s.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0);

        // Fires on every add, remove, move and filter refresh.
        FilteredSessions.CollectionChanged += (_, _) => UpdatePinnedBoundary();
    }

    /// <summary>
    /// Marks the row where the pinned group ends. Works on the visible list,
    /// so a search that hides every pinned row also hides the separator.
    /// </summary>
    private void UpdatePinnedBoundary()
    {
        var pinnedAbove = false;
        var marked = false;

        foreach (SessionInfo session in FilteredSessions)
        {
            var starts = !marked && pinnedAbove && !session.IsPinned;
            session.StartsUnpinnedGroup = starts;
            marked |= starts;
            pinnedAbove |= session.IsPinned;
        }
    }

    partial void OnSearchTextChanged(string value) => FilteredSessions.Refresh();

    /// <summary>
    /// Initializes the view model with a session store and folder path for persistence.
    /// </summary>
    public void Initialize(ISessionStore sessionStore, string folderPath)
    {
        _sessionStore = sessionStore;
        _folderPath = folderPath;
    }

    /// <summary>
    /// Loads previously saved sessions from the store into the Sessions collection.
    /// </summary>
    public async Task LoadSessionsAsync()
    {
        if (_sessionStore is null || _folderPath is null) return;

        var entries = await _sessionStore.GetSessionIndexAsync(_folderPath);
        Sessions.Clear();

        foreach (var entry in entries
                     .OrderByDescending(e => e.IsPinned)
                     .ThenByDescending(e => e.LastActivityUtc))
        {
            var info = new SessionInfo
            {
                PersistedId = entry.Id,
                Name = entry.Title,
                HasCustomTitle = entry.TitleIsCustom,
                IsPinned = entry.IsPinned,
                LastActivity = entry.LastActivityUtc.ToLocalTime(),
                IsActive = false
            };

            Sessions.Add(info);
        }
    }

    [RelayCommand]
    public async Task NewSessionAsync()
    {
        var session = new SessionInfo
        {
            Name = $"Chat {Sessions.Count + 1}",
            IsActive = true
        };

        if (_sessionStore is not null && _folderPath is not null)
        {
            try
            {
                var entry = await _sessionStore.CreateSessionAsync(_folderPath, session.Name);
                session.PersistedId = entry.Id;
            }
            catch { /* best effort — session works in-memory even if persistence fails */ }
        }

        // Newest first, but below whatever the user pinned.
        Sessions.Insert(Sessions.TakeWhile(s => s.IsPinned).Count(), session);
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
    }

    /// <summary>
    /// Pins or unpins a session and moves it to its place in the list.
    /// </summary>
    [RelayCommand]
    private async Task TogglePinAsync(SessionInfo? session)
    {
        if (session is null) return;

        session.IsPinned = !session.IsPinned;
        ApplyPinnedOrder();

        // A row already in place does not move, so the list raises no change.
        UpdatePinnedBoundary();

        if (session.PersistedId.HasValue && _sessionStore is not null && _folderPath is not null)
        {
            try
            {
                var index = await _sessionStore.GetSessionIndexAsync(_folderPath);
                var entry = index.FirstOrDefault(e => e.Id == session.PersistedId.Value);
                if (entry is not null)
                {
                    entry.IsPinned = session.IsPinned;
                    await _sessionStore.UpdateSessionAsync(_folderPath, entry);
                }
            }
            catch { /* best effort — the order still holds for this session */ }
        }
    }

    /// <summary>
    /// Moves pinned sessions to the top. OrderBy is stable, so the relative
    /// order inside each group survives — pinning and unpinning only ever
    /// moves the one row across the boundary.
    /// </summary>
    private void ApplyPinnedOrder()
    {
        var ordered = Sessions.OrderByDescending(s => s.IsPinned).ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Sessions.IndexOf(ordered[target]);
            if (current != target)
                Sessions.Move(current, target);
        }
    }

    [RelayCommand]
    private void OpenSession(SessionInfo? session)
    {
        if (session is null) return;
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
    }

    /// <summary>
    /// Puts a row into inline rename mode. Any other row being renamed is
    /// closed first, so only one rename box is ever open.
    /// </summary>
    [RelayCommand]
    private void BeginRename(SessionInfo? session)
    {
        if (session is null) return;

        foreach (var other in Sessions)
        {
            if (!ReferenceEquals(other, session))
                other.IsRenaming = false;
        }

        session.EditingName = session.Name;
        session.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename(SessionInfo? session)
    {
        if (session is null) return;
        session.IsRenaming = false;
        session.EditingName = session.Name;
    }

    /// <summary>
    /// Applies the typed name and persists it. A blank or unchanged name is
    /// treated as a cancel.
    /// </summary>
    [RelayCommand]
    private async Task CommitRenameAsync(SessionInfo? session)
    {
        if (session is null || !session.IsRenaming) return;

        session.IsRenaming = false;

        var newName = session.EditingName?.Trim() ?? string.Empty;
        if (newName.Length == 0 || newName == session.Name)
        {
            session.EditingName = session.Name;
            return;
        }

        session.Name = newName;
        session.HasCustomTitle = true;

        // The name is part of the filter predicate, so a rename can move the
        // row in or out of the current search results.
        FilteredSessions.Refresh();

        if (session.PersistedId.HasValue && _sessionStore is not null && _folderPath is not null)
        {
            try
            {
                var index = await _sessionStore.GetSessionIndexAsync(_folderPath);
                var entry = index.FirstOrDefault(e => e.Id == session.PersistedId.Value);
                if (entry is not null)
                {
                    // Leave LastActivityUtc alone — renaming is not activity and
                    // must not reorder the list.
                    entry.Title = newName;
                    entry.TitleIsCustom = true;
                    await _sessionStore.UpdateSessionAsync(_folderPath, entry);
                }
            }
            catch { /* best effort — the new name still shows in this session */ }
        }
    }

    [RelayCommand]
    private async Task RemoveSessionAsync(SessionInfo? session)
    {
        if (session is null) return;
        if (ConfirmDelete is not null && !ConfirmDelete(session)) return;

        session.IsActive = false;
        Sessions.Remove(session);
        SessionRemoved?.Invoke(session);

        if (session.PersistedId.HasValue && _sessionStore is not null && _folderPath is not null)
        {
            try
            {
                await _sessionStore.DeleteSessionAsync(_folderPath, session.PersistedId.Value);
            }
            catch { /* best effort */ }
        }

        if (SelectedSession == session)
            SelectedSession = Sessions.FirstOrDefault();
    }
}
