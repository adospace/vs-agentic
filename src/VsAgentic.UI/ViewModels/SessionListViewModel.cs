using System.Collections.ObjectModel;
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
}

public partial class SessionListViewModel : ObservableObject
{
    private ISessionStore? _sessionStore;
    private string? _folderPath;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    public event Action<SessionInfo>? SessionOpenRequested;
    public event Action<SessionInfo>? SessionRemoved;

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
            Sessions.Add(new SessionInfo
            {
                PersistedId = entry.Id,
                Name = entry.Title,
                LastActivity = entry.LastActivityUtc.ToLocalTime(),
                IsActive = false
            });
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

        Sessions.Add(session);
        SelectedSession = session;
        SessionOpenRequested?.Invoke(session);
    }

    [RelayCommand]
    private void OpenSession(SessionInfo? session)
    {
        if (session is null) return;
        SelectedSession = session;
        session.LastActivity = DateTime.Now;
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
