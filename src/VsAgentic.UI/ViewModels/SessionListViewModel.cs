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

    [ObservableProperty]
    private bool _isActive;

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

    public SessionListViewModel()
    {
        FilteredSessions = CollectionViewSource.GetDefaultView(Sessions);
        FilteredSessions.Filter = obj =>
            obj is SessionInfo s
            && (string.IsNullOrWhiteSpace(SearchText)
                || s.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0);
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

        foreach (var entry in entries.OrderByDescending(e => e.LastActivityUtc))
        {
            var info = new SessionInfo
            {
                PersistedId = entry.Id,
                Name = entry.Title,
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

        Sessions.Insert(0, session);
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
    }

    [RelayCommand]
    private void OpenSession(SessionInfo? session)
    {
        if (session is null) return;
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
    }

    [RelayCommand]
    private async Task RemoveSessionAsync(SessionInfo? session)
    {
        if (session is null) return;
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
