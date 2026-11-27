using System.ComponentModel;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using VsAgentic.UI.ViewModels;
using VsAgentic.UI.ViewModels.Banners;
using VsAgentic.VSExtension.Options;

namespace VsAgentic.VSExtension.Notifications;

/// <summary>
/// Calls the user back to a session from outside Visual Studio: a Windows
/// notification and a flashing taskbar button when a session starts waiting
/// for them or finishes a turn they did not see, while Visual Studio is not
/// the window they work in. Inside Visual Studio the tab title and the status
/// bar already say the same (see <see cref="ChatSessionViewModel.Activity"/>).
/// </summary>
/// <remarks>
/// Driven by the session's own state, not by anything the agent's CLI reports,
/// so it works the same for any agent behind the session. All members run on
/// the UI thread.
/// </remarks>
internal sealed class AttentionNotifier : IDisposable
{
    // Text naming the agent, in one place so another agent can be named instead.
    private const string AgentName = "Claude";

    private readonly VsAgenticOptionsPage _options;
    private readonly WindowsToast? _toast;
    private readonly Func<string?> _solutionName;
    private readonly Func<SessionInfo, Task> _openSession;
    private readonly Action<string, Exception?> _log;
    private readonly Dictionary<ChatSessionViewModel, Watch> _watches = new();
    private bool _flashing;

    public AttentionNotifier(
        VsAgenticOptionsPage options,
        WindowsToast? toast,
        Func<string?> solutionName,
        Func<SessionInfo, Task> openSession,
        Action<string, Exception?> log)
    {
        _options = options;
        _toast = toast;
        _solutionName = solutionName;
        _openSession = openSession;
        _log = log;

        // A question that came up while the user was in Visual Studio, but not
        // in the session, is announced when they leave without answering it.
        if (Application.Current is { } app)
        {
            app.Deactivated += OnApplicationDeactivated;
            app.Activated += OnApplicationActivated;
        }
    }

    public void Track(ChatSessionViewModel viewModel, SessionInfo session)
    {
        if (_watches.ContainsKey(viewModel)) return;

        var watch = new Watch(viewModel, session, "vsagentic-" + session.Id);
        watch.Handler = (_, e) =>
        {
            if (e.PropertyName == nameof(ChatSessionViewModel.Activity))
                OnActivityChanged(watch);
        };
        viewModel.PropertyChanged += watch.Handler;
        _watches[viewModel] = watch;
        OnActivityChanged(watch);
    }

    public void Untrack(ChatSessionViewModel viewModel)
    {
        if (!_watches.TryGetValue(viewModel, out var watch)) return;

        viewModel.PropertyChanged -= watch.Handler;
        _watches.Remove(viewModel);
        _toast?.Remove(watch.Tag);
        UpdateFlash();
    }

    private void OnActivityChanged(Watch watch)
    {
        var activity = watch.ViewModel.Activity;
        if (activity == watch.Activity) return;
        watch.Activity = activity;

        // Whatever the toast said is out of date now: the question is answered,
        // the turn is seen, or the session moved on to the next thing.
        _toast?.Remove(watch.Tag);
        watch.Announced = false;

        if (IsCallingBack(activity)) Announce(watch);
        UpdateFlash();
    }

    // Windows raises Deactivated inside WM_ACTIVATEAPP, an input-synchronous
    // call in which COM calls fail with RPC_E_CANTCALLOUT_ININPUTSYNCCALL, and
    // building a toast is one. The announcement waits until that call returns.
    private void OnApplicationDeactivated(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(AnnounceWaiting));

    private void AnnounceWaiting()
    {
        foreach (var watch in _watches.Values)
        {
            if (watch.Activity == SessionActivity.AwaitingUser && !watch.Announced)
                Announce(watch);
        }
        UpdateFlash();
    }

    // Windows stops the flash itself when the window comes to the front; this
    // only keeps the flag in step so the next announcement starts a new one.
    private void OnApplicationActivated(object? sender, EventArgs e) => _flashing = false;

    private void Announce(Watch watch)
    {
        if (MainWindowAttention.IsInForeground()) return;
        watch.Announced = true;

        var enabled = watch.Activity == SessionActivity.AwaitingUser
            ? _options.NotifyWhenWaiting
            : _options.NotifyWhenComplete;
        if (!enabled || _toast is null) return;

        var solution = _solutionName();
        var attribution = string.IsNullOrEmpty(solution) ? "VsAgentic" : $"VsAgentic · {solution}";
        var session = watch.Session;

        _toast.Show(watch.Tag, session.Name, Describe(watch), attribution, () =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                MainWindowAttention.BringToFront();
                await _openSession(session);
            }).FileAndForget("VsAgentic/NotificationClick"));
    }

    private static string Describe(Watch watch) => watch.Activity switch
    {
        SessionActivity.AwaitingUser => watch.ViewModel.ActiveBanner switch
        {
            // The plan banner is a permission banner, so it goes first.
            PlanBannerViewModel => $"{AgentName} has a plan for you to review",
            PermissionBannerViewModel => $"{AgentName} asks for permission",
            QuestionCardViewModel => $"{AgentName} has a question for you",
            LoginBannerViewModel => $"{AgentName} needs you to sign in",
            _ => $"{AgentName} is waiting for you",
        },
        _ => $"{AgentName} has finished",
    };

    /// <summary>
    /// Flashes while any session waits for the user or has an unseen turn, and
    /// stops once none does. Starting a flash that already runs is skipped, so
    /// a second session does not restart the count.
    /// </summary>
    private void UpdateFlash()
    {
        var wanted = _options.FlashTaskbarButton
            && !MainWindowAttention.IsInForeground()
            && _watches.Values.Any(w => w.Announced && IsCallingBack(w.Activity));

        if (wanted == _flashing) return;
        _flashing = wanted;
        MainWindowAttention.Flash(wanted);
    }

    private static bool IsCallingBack(SessionActivity activity) =>
        activity is SessionActivity.AwaitingUser or SessionActivity.Completed;

    public void Dispose()
    {
        if (Application.Current is { } app)
        {
            app.Deactivated -= OnApplicationDeactivated;
            app.Activated -= OnApplicationActivated;
        }

        foreach (var watch in _watches.Values)
            watch.ViewModel.PropertyChanged -= watch.Handler;
        _watches.Clear();

        // A toast left behind would bring back a Visual Studio that is gone.
        try { _toast?.RemoveAll(); }
        catch (Exception ex) { _log("Could not clear the Windows notifications", ex); }
    }

    private sealed class Watch
    {
        public Watch(ChatSessionViewModel viewModel, SessionInfo session, string tag)
        {
            ViewModel = viewModel;
            Session = session;
            Tag = tag;
        }

        public ChatSessionViewModel ViewModel { get; }
        public SessionInfo Session { get; }

        /// <summary>Tag of the session's toast; a new toast replaces the old one.</summary>
        public string Tag { get; }

        public PropertyChangedEventHandler Handler { get; set; } = delegate { };
        public SessionActivity Activity { get; set; } = SessionActivity.Idle;

        /// <summary>
        /// Whether the current state was announced, or deliberately not because
        /// its notification is off. False while the user was in Visual Studio
        /// when the state began, so leaving it still announces a question.
        /// </summary>
        public bool Announced { get; set; }
    }
}
