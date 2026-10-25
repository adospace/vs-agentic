using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
using VsAgentic.Services.Configuration;
using VsAgentic.Services.Models;
using VsAgentic.UI.ViewModels.Banners;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VsAgentic.UI.ViewModels;

public partial class ChatSessionViewModel : ObservableObject, IDisposable
{
    private readonly IChatService? _chatService;
    private readonly VsAgenticOptions _options;
    private IDisposable? _serviceScope;
    private readonly ConcurrentDictionary<string, ChatItemViewModel> _activeItems = new();
    private int _userMsgCounter;

    public ObservableCollection<ChatItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    private CancellationTokenSource? _sendCts;

    [ObservableProperty]
    private string _sessionTitle = "New Session";

    /// <summary>
    /// True when <see cref="SessionTitle"/> was typed by the user in the
    /// session list. The title generated from the first message is then
    /// skipped so the manual name survives.
    /// </summary>
    public bool HasCustomTitle { get; set; }

    // Realtime activity indicator: the tool window caption carries an animated
    // prefix describing what the session is doing. Host bindings (e.g. the VS
    // tool window caption) should use DisplayTitle; SessionTitle stays plain
    // for the session list entry so the sidebar doesn't flicker.
    //
    // One animation runs at a time — whichever TitleAnimations maps the current
    // Activity to — so the tick counter only ever serves that one and is reset
    // on every transition. Animations therefore never have to be kept in phase
    // with each other, and adding one is an entry in the table below.

    /// <summary>
    /// One caption animation. Frames must be equal width and carry their own
    /// trailing space: the caption is a plain string with no way to hide a
    /// glyph, so a narrower frame shifts the title text every time it shows.
    /// </summary>
    /// <param name="Frames">Glyphs to cycle through, in order.</param>
    /// <param name="TicksPerFrame">Timer ticks each frame is held for.</param>
    /// <param name="DurationTicks">
    /// Ticks before the animation stops on its own, for states that are
    /// transient rather than held. Null loops until the state changes.
    /// </param>
    private sealed record TitleAnimation(string[] Frames, int TicksPerFrame, int? DurationTicks = null)
    {
        /// <summary>Ticks for one full pass through <see cref="Frames"/>.</summary>
        public int CycleTicks => Frames.Length * TicksPerFrame;

        public string FrameAt(int tick) => Frames[(tick / TicksPerFrame) % Frames.Length];

        public bool HasExpired(int tick) => DurationTicks is int d && tick >= d;
    }

    private static readonly string[] SpinnerFrames =
        { "⠋ ", "⠙ ", "⠹ ", "⠸ ", "⠼ ", "⠴ ", "⠦ ", "⠧ ", "⠇ ", "⠏ " };

    // Two hand glyphs rather than a hand alternating with blank, for the
    // equal-width reason above. The trailing U+FE0F on the raised hand forces
    // emoji presentation — its text-presentation fallback is narrower.
    private static readonly string[] AwaitingFrames = { "👋 ", "✋️ " };

    // Shown in place of the hand when AnimateTitleWhileWaiting is off. Unlike
    // Busy, AwaitingUser exists to make a backgrounded window visibly blocked
    // on the user — turning the animation off should stop the motion, not
    // remove the signal, so this keeps the pre-animation "? " prefix as a
    // static fallback rather than falling through to no prefix at all.
    private const string AwaitingStaticPrefix = "? ";

    // Sparkle pulse for a turn that just finished. It and the status bar flash
    // clear together the moment the window is looked at (see NotifySessionSeen),
    // the same way the waiting hand clears when the banner it represents gets
    // resolved.
    private static readonly string[] CompletedFrames = { "✨ ", "⭐ " };

    // The Completed cue when ShowCompletedIndicator is on but the animation is
    // off — the same arrangement as AwaitingStaticPrefix, except the switch
    // that silences it entirely is its own rather than the animation's. Its own
    // glyph rather than a frozen CompletedFrames entry, for the same reason the
    // waiting fallback is a "?" and not a frozen hand: standing still, a mark
    // that means "finished" reads better than one that only meant motion.
    private const string CompletedStaticPrefix = "✔ ";

    // The timer ticks at spinner speed, so anything slower asks for a larger
    // TicksPerFrame rather than its own timer. The hand at 4 lands near 0.5s,
    // which reads as a wave instead of a strobe.
    private static readonly IReadOnlyDictionary<SessionActivity, TitleAnimation> TitleAnimations =
        new Dictionary<SessionActivity, TitleAnimation>
        {
            [SessionActivity.Busy] = new(SpinnerFrames, TicksPerFrame: 1),
            [SessionActivity.AwaitingUser] = new(AwaitingFrames, TicksPerFrame: 4),
            [SessionActivity.Completed] = new(CompletedFrames, TicksPerFrame: 4),
        };

    private int _tick;
    private SessionActivity _lastActivity = SessionActivity.Idle;

    /// <summary>
    /// Title of a session whose first message is an image with no text, until
    /// a message with text gives the title model something to summarize.
    /// </summary>
    private const string PastedImageTitle = "Pasted image";

    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    // Set when a turn ends unwatched, and cleared the instant the window is
    // looked at. A separate flag rather than folding into Activity's other
    // inputs because, unlike Busy/IsAwaitingUser, nothing about the
    // chat service's state says "seen" — only the UI knows that.
    private bool _turnUnseen;

    /// <summary>
    /// Whether the user is looking at this session right now. Maintained by
    /// the host, which is the only thing that can see focus; a turn that ends
    /// while this is true never arms the Completed indicator, because there is
    /// nobody to call back to a window they are already in.
    /// </summary>
    public bool IsSessionFocused { get; set; }

    [ObservableProperty]
    private string _displayTitle = "New Session";

    /// <summary>
    /// What the AI is doing right now, shown in the bar under the chat: the
    /// step in flight and how long the turn has been running. A long task
    /// reports itself here rather than by filling the transcript.
    /// </summary>
    [ObservableProperty]
    private string _busyStatus = "Thinking...";

    /// <summary>
    /// When the current turn started. The bar counts from here, not from the
    /// start of the step, so the number answers "how long have I been waiting".
    /// </summary>
    private DateTime? _turnStartedUtc;

    /// <summary>
    /// Title of the step in flight, or null between steps. Held with the id
    /// that set it so a step completing out of order cannot blank the label
    /// belonging to its successor.
    /// </summary>
    private string? _currentStep;
    private string? _currentStepId;

    /// <summary>
    /// The user is being waited on exactly while a banner is on screen. Read
    /// from the banner rather than counted: a counter has to be decremented on
    /// every path that takes a banner down — answered, cancelled by Stop, the
    /// handler throwing, one banner replacing another — and a single missed
    /// decrement leaves the session claiming it waits for an answer the user
    /// has already given.
    /// </summary>
    private bool IsAwaitingUser => ActiveBanner is not null;

    public SessionActivity Activity =>
        IsAwaitingUser ? SessionActivity.AwaitingUser :
        IsBusy ? SessionActivity.Busy :
        _turnUnseen ? SessionActivity.Completed :
        SessionActivity.Idle;

    /// <summary>
    /// Called by the host when the user gives the chat window their attention —
    /// focus landing inside it, or a click anywhere in it. Clears a pending
    /// Completed indicator: the caption prefix and the flash both key off
    /// <see cref="Activity"/>, so this is the one place that needs to know
    /// what "seen" means. Idempotent, since the host raises it far more often
    /// than there is an indicator to clear.
    /// </summary>
    public void NotifySessionSeen()
    {
        if (!_turnUnseen) return;
        _turnUnseen = false;
        UpdateActivityIndicator();
    }

    /// <summary>
    /// The animation for the current state, or null when the state has none or
    /// the user has switched it off.
    /// </summary>
    private TitleAnimation? CurrentAnimation =>
        TitleAnimations.TryGetValue(Activity, out var animation) && IsAnimationEnabled(Activity)
            ? animation
            : null;

    private bool IsAnimationEnabled(SessionActivity activity) => activity switch
    {
        SessionActivity.Busy => _options.AnimateTitleWhileBusy,
        SessionActivity.AwaitingUser => _options.AnimateTitleWhileWaiting,
        // The marker switch gates both forms, so turning it off stops the timer
        // rather than leaving it running against a prefix nothing will show.
        SessionActivity.Completed => _options.ShowCompletedIndicator && _options.AnimateTitleWhenComplete,
        _ => false
    };

    /// <summary>
    /// The prefix for a state whose animation is switched off but which still
    /// has something to say. Everything the caption can show for a state is
    /// either here or in <see cref="TitleAnimations"/>.
    /// </summary>
    private string StaticPrefix => Activity switch
    {
        SessionActivity.AwaitingUser when !_options.AnimateTitleWhileWaiting => AwaitingStaticPrefix,
        SessionActivity.Completed when _options.ShowCompletedIndicator => CompletedStaticPrefix,
        _ => ""
    };

    /// <summary>
    /// Pushes Tools → Options → Appearance changes into an already-open session.
    /// <see cref="_options"/> is a private copy captured when the session's tool
    /// window was created, so without this a toggle only takes effect on the
    /// next session opened rather than the one currently on screen.
    /// </summary>
    public void ApplyAppearanceOptions(
        bool animateTitleWhileBusy,
        bool animateTitleWhileWaiting,
        bool flashStatusBarWhileWaiting,
        bool showCompletedIndicator,
        bool animateTitleWhenComplete,
        bool flashStatusBarWhenComplete)
    {
        _options.AnimateTitleWhileBusy = animateTitleWhileBusy;
        _options.AnimateTitleWhileWaiting = animateTitleWhileWaiting;
        _options.FlashStatusBarWhileWaiting = flashStatusBarWhileWaiting;
        _options.ShowCompletedIndicator = showCompletedIndicator;
        _options.AnimateTitleWhenComplete = animateTitleWhenComplete;
        _options.FlashStatusBarWhenComplete = flashStatusBarWhenComplete;
        UpdateActivityIndicator();
    }

    /// <summary>Text for the indicator strip under the chat input; empty hides it.</summary>
    // While a turn runs, BusyStatus already covers both Busy and a banner being
    // up (step or "Waiting for you", plus the elapsed time).
    public string StatusText => Activity switch
    {
        _ when IsBusy => BusyStatus,
        SessionActivity.AwaitingUser => "Waiting for input…",
        SessionActivity.Completed => "Done",
        _ => ""
    };

    /// <summary>Whether the status bar should be pulsing. Bound by ChatSessionControl.xaml.</summary>
    public bool IsFlashing =>
        (Activity == SessionActivity.AwaitingUser && _options.FlashStatusBarWhileWaiting)
        || (Activity == SessionActivity.Completed && _options.FlashStatusBarWhenComplete);

    partial void OnBusyStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsBusyChanged(bool value)
    {
        // Every turn end counts, not just clean ones and not just turns the
        // user sent — Stop, an error and a turn the CLI starts on its own all
        // still leave the window worth glancing back at. Unless the user is
        // already in it, in which case they have just watched the turn end and
        // there is nothing left to announce.
        if (!value) _turnUnseen = !IsSessionFocused;

        // Both the elapsed count and the step label belong to one turn.
        _turnStartedUtc = value ? DateTime.UtcNow : null;
        _currentStep = null;
        _currentStepId = null;
        UpdateActivityIndicator();
    }

    partial void OnActiveBannerChanged(IBannerViewModel? value) => UpdateActivityIndicator();

    partial void OnSessionTitleChanged(string value) => UpdateDisplayTitle();

    private void UpdateActivityIndicator()
    {
        // Activity and everything derived from it are computed, so they have to
        // be notified by hand for ChatSessionControl.xaml to see the change.
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsFlashing));

        // Only on a real transition: this runs on every permission request and
        // every resolve, and restarting the tick each time would visibly reset
        // the spinner mid-turn.
        if (Activity != _lastActivity)
        {
            _lastActivity = Activity;
            _tick = 0;
        }

        // IsBusy as well as the animation: the elapsed clock has to keep running
        // while a permission or question banner is up, and when the user has
        // switched the busy spinner off.
        if (IsBusy || CurrentAnimation is not null)
        {
            EnsureActivityTimer();
            if (!_activityTimer!.IsEnabled) _activityTimer.Start();
        }
        else
        {
            _activityTimer?.Stop();
        }
        UpdateDisplayTitle();
        UpdateBusyStatus();
    }

    private void EnsureActivityTimer()
    {
        if (_activityTimer != null) return;
        var dispatcher = Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _activityTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _activityTimer.Tick += (_, _) =>
        {
            var animation = CurrentAnimation;
            if (animation is not null)
            {
                // A held animation wraps so the counter stays bounded; a transient
                // one must keep counting up or it can never reach its duration.
                _tick = animation.DurationTicks is null
                    ? (_tick + 1) % animation.CycleTicks
                    : _tick + 1;
            }

            // Kept running while busy so the elapsed clock under the chat ticks on.
            if (!IsBusy && (animation is null || animation.HasExpired(_tick)))
                _activityTimer!.Stop();

            UpdateDisplayTitle();
            UpdateBusyStatus();
        };
    }

    /// <summary>
    /// Rebuilds the line under the chat. Called on every spinner tick; the
    /// text only changes once a second, and the generated setter drops a write
    /// that would not change anything, so the binding is not woken for nothing.
    /// </summary>
    private void UpdateBusyStatus()
    {
        if (!IsBusy)
        {
            BusyStatus = "Thinking...";
            return;
        }

        var label = IsAwaitingUser
            ? "Waiting for you"
            : _currentStep ?? "Thinking...";

        if (_turnStartedUtc is null)
        {
            BusyStatus = label;
            return;
        }

        var elapsed = DateTime.UtcNow - _turnStartedUtc.Value;
        BusyStatus = $"{label} · {FormatElapsed(elapsed)}";
    }

    /// <summary>
    /// Seconds up to a minute, then minutes and seconds — a bare "384s" stops
    /// meaning anything once a task has been running for a while.
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        var total = (int)elapsed.TotalSeconds;
        if (total < 0) total = 0;
        return total < 60 ? $"{total}s" : $"{total / 60}m {total % 60}s";
    }

    private void UpdateDisplayTitle()
    {
        var animation = CurrentAnimation;
        var prefix = animation is not null && !animation.HasExpired(_tick)
            ? animation.FrameAt(_tick)
            : StaticPrefix;
        DisplayTitle = prefix + SessionTitle;
    }

    public string WorkingDirectory { get; }

    /// <summary>
    /// The <see cref="SessionInfo"/> entry in the session list that owns this view model.
    /// When set, cost is updated on the entry after each completed message exchange.
    /// </summary>
    public SessionInfo? SessionInfo { get; set; }

    public event Action? ScrollRequested;

    // Events for single-WebView rendering
    public event Action<string, ChatItemType, ChatMessageData>? MessageAdded;
    public event Action<string, string>? MessageContentUpdated;
    public event Action<string, OutputItemStatus, string>? MessageStatusUpdated;
    public event Action<string, string, OutputBodyMode>? MessageBodySet;
    public event Action<string, BackgroundStepState, string?>? MessageBackgroundStateSet;
    public event Action<string>? MessageCompleted;
    public event Action? AllCleared;

    /// <summary>
    /// Raised once the turn is over, so the view can stop presenting the last
    /// run of steps as still in flight.
    /// </summary>
    public event Action? TurnEnded;
    public event Action<IEnumerable<ChatMessageData>>? MessagesRestored;

    /// <summary>
    /// The banner currently shown above the input box (permission prompt,
    /// AskUserQuestion card, or login prompt). The host's ContentControl
    /// binds to this; concrete type is selected by DataTemplate. Null when
    /// no banner is active.
    /// </summary>
    [ObservableProperty]
    private IBannerViewModel? _activeBanner;

    private readonly IPermissionBroker? _permissionBroker;
    private readonly IUserQuestionBroker? _questionBroker;
    private readonly ILogger _logger;

    /// <summary>
    /// Standalone constructor for use without a chat service (e.g. before service is wired up).
    /// </summary>
    public ChatSessionViewModel(string workingDirectory = "")
    {
        _options = new VsAgenticOptions { WorkingDirectory = workingDirectory };
        WorkingDirectory = workingDirectory;
        _logger = NullLogger.Instance;
    }

    public ChatSessionViewModel(IChatService chatService, OutputListener outputListener, IOptions<VsAgenticOptions> options)
        : this(chatService, outputListener, options, permissionBroker: null, questionBroker: null, logger: null)
    {
    }

    public ChatSessionViewModel(
        IChatService chatService,
        OutputListener outputListener,
        IOptions<VsAgenticOptions> options,
        IPermissionBroker? permissionBroker,
        IUserQuestionBroker? questionBroker,
        ILogger<ChatSessionViewModel>? logger = null)
    {
        _chatService = chatService;
        _options = options.Value;
        WorkingDirectory = _options.WorkingDirectory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        outputListener.StepStarted += OnStepStarted;
        outputListener.StepUpdated += OnStepUpdated;
        outputListener.StepCompleted += OnStepCompleted;

        _permissionBroker = permissionBroker;
        _questionBroker = questionBroker;

        if (_permissionBroker is not null)
        {
            _permissionBroker.PermissionRequested += OnPermissionBrokerRequested;
            _permissionBroker.PendingCancelled += OnPermissionCancelled;
        }
        if (_questionBroker is not null)
        {
            _questionBroker.QuestionRequested += OnQuestionBrokerRequested;
            _questionBroker.PendingCancelled += OnQuestionCancelled;
        }

        chatService.LoginRequired += OnChatServiceLoginRequired;
        chatService.UnsolicitedTurnStarted += OnUnsolicitedTurnStarted;
        chatService.UnsolicitedTurnCompleted += OnUnsolicitedTurnCompleted;
        chatService.BackgroundTasksChanged += OnChatServiceBackgroundTasksChanged;
        chatService.StepBackgroundStateChanged += OnChatServiceStepBackgroundStateChanged;
        chatService.StepCanMoveToBackground += OnChatServiceStepCanMoveToBackground;
        chatService.StepFileChanged += OnChatServiceStepFileChanged;

        InitializeUsage(chatService, options.Value);
    }

    // True while a turn the CLI started on its own is running. It holds the
    // session busy like a sent message does, so Send stays off and Stop works.
    //
    // Such a turn can start while SendAsync is still closing the turn before
    // it (the service already has that turn's result). Whichever of the two
    // ends last clears IsBusy and ends the turn in the view.
    private bool _inUnsolicitedTurn;

    private void OnUnsolicitedTurnStarted()
    {
        Dispatch(() =>
        {
            _inUnsolicitedTurn = true;
            IsBusy = true;
        });
    }

    private void OnUnsolicitedTurnCompleted()
    {
        Dispatch(() =>
        {
            if (!_inUnsolicitedTurn) return;
            _inUnsolicitedTurn = false;
            OnExchangeCompleted();

            // A sent message still holds the session; SendAsync ends the turn.
            if (_sendCts is not null) return;

            IsBusy = false;
            TurnEnded?.Invoke();
            RequestScroll();
        });
    }

    private void OnExchangeCompleted()
    {
        // Persist conversation history after each completed exchange
        PersistConversationHistoryFireAndForget();

        // Refresh cost and last activity in the session list
        if (_chatService is not null && SessionInfo is not null)
        {
            SessionInfo.SessionCost = _chatService.GetSessionCost();
            SessionInfo.LastActivity = DateTime.Now;
        }
    }

    // The step id is the id the transcript already knows the step by, so the
    // mark goes straight to the web view. It is not saved: a reopened session
    // shows the step without it.
    //
    // When the work ends, a notice line with the outcome is added as well. The
    // step may sit folded away, or far up the transcript; the notice lands
    // where the user is reading, just above the answer the CLI wakes up to
    // give. Unlike the mark it is saved with the session.
    //
    // A shell command's notice also carries the file the CLI wrote its output
    // to, so the user can open the whole of it.
    private void OnChatServiceStepBackgroundStateChanged(string stepId, BackgroundStepState state, string? summary, string? outputFile)
    {
        Dispatch(() =>
        {
            MessageBackgroundStateSet?.Invoke(stepId, state, summary);
            if (state != BackgroundStepState.Running)
                AddNotice(state, string.IsNullOrWhiteSpace(summary) ? $"Background command {state.ToString().ToLowerInvariant()}." : summary!, outputFile);
        });
    }

    /// <summary>
    /// Raised when a running step can be moved to the background. The view
    /// offers it on the step; the argument is the step id.
    /// </summary>
    public event Action<string>? MessageCanMoveToBackground;

    private void OnChatServiceStepCanMoveToBackground(string stepId) =>
        Dispatch(() => MessageCanMoveToBackground?.Invoke(stepId));

    /// <summary>
    /// Moves a running step's command or subagent to the background. Called
    /// from the step's button in the transcript.
    /// </summary>
    public async Task MoveStepToBackgroundAsync(string stepId)
    {
        if (_chatService is null) return;
        try { await _chatService.MoveStepToBackgroundAsync(stepId); }
        catch (Exception ex) { _logger.LogWarning(ex, "[VM] Could not move step {Id} to the background", stepId); }
    }

    // ── File changes ──────────────────────────────────────────────────────
    // What Claude's Edit, MultiEdit and Write calls did to files, by step id,
    // for the step's Diff button. Restored steps are keyed by their restore id.
    // Touched on the UI thread only.
    private readonly Dictionary<string, FileChange> _fileChanges = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised when a step gets a file change to show as a diff. The argument
    /// is the step id.
    /// </summary>
    public event Action<string>? MessageFileChangeSet;

    /// <summary>
    /// Raised when the user asks to see a diff, from a step or from the
    /// permission banner. The host opens it in its diff viewer.
    /// </summary>
    public event Action<FileDiffContent>? FileDiffRequested;

    private void OnChatServiceStepFileChanged(string stepId, FileChange change)
    {
        Dispatch(() =>
        {
            _fileChanges[stepId] = change;
            MessageFileChangeSet?.Invoke(stepId);

            // A step still open saves the change when it completes. One that
            // is closed was saved without it: when Claude makes several calls
            // in one message and the first waits for permission, the CLI
            // starts them all, and each closes the step before it.
            if (!_activeItems.ContainsKey(stepId))
                PersistStepFileChangeFireAndForget(stepId, change);
        });
    }

    /// <summary>
    /// Opens the diff of a step's file change. Called from the step's button
    /// in the transcript.
    /// </summary>
    public void OpenStepDiff(string stepId)
    {
        if (!_fileChanges.TryGetValue(stepId, out var change)) return;
        if (FileChanges.ForStep(change) is { } diff)
            FileDiffRequested?.Invoke(diff);
    }

    private void AddNotice(BackgroundStepState state, string text, string? outputFile = null)
    {
        // The status picks the icon and color in the web view.
        var status = state switch
        {
            BackgroundStepState.Completed => OutputItemStatus.Success,
            BackgroundStepState.Failed => OutputItemStatus.Error,
            _ => OutputItemStatus.Info,
        };
        var id = $"notice-{Guid.NewGuid():N}";
        Items.Add(new ChatItemViewModel
        {
            Type = ChatItemType.Notice,
            Content = text,
            // A notice has no body of its own; the web view shows this as a
            // link to the output.
            Body = outputFile,
            Status = status,
            IsStreaming = false
        });
        MessageAdded?.Invoke(id, ChatItemType.Notice, new ChatMessageData
        {
            Id = id,
            Type = ChatItemType.Notice.ToString(),
            Content = text,
            Body = outputFile,
            Status = status.ToString()
        });
        RequestScroll();

        PersistMessageFireAndForget(new PersistedMessage
        {
            ItemType = ChatItemType.Notice.ToString(),
            Content = text,
            Body = outputFile,
            StatusText = status.ToString(),
            CreatedUtc = DateTime.UtcNow
        });
    }

    // ── Background tasks ──────────────────────────────────────────────────
    // Commands and subagents the CLI runs in the background. They outlive the
    // turn that started them, so the strip above the input box is the only
    // place that shows they are still running, and where each can be stopped.

    [ObservableProperty]
    private bool _hasBackgroundTasks;

    /// <summary>One row per running background task, in the CLI's order.</summary>
    public ObservableCollection<BackgroundTaskItemViewModel> BackgroundTaskItems { get; } = new();

    private void OnChatServiceBackgroundTasksChanged(IReadOnlyList<BackgroundTask> tasks)
    {
        Dispatch(() =>
        {
            // The CLI sends the whole list each time. Rows of tasks that are
            // still running are kept, so a Stop already pressed stays pressed.
            var kept = BackgroundTaskItems.ToDictionary(i => i.Id, StringComparer.Ordinal);
            BackgroundTaskItems.Clear();
            foreach (var task in tasks)
            {
                BackgroundTaskItems.Add(kept.TryGetValue(task.Id, out var row)
                    ? row
                    : new BackgroundTaskItemViewModel(task, _chatService!, _logger));
            }
            HasBackgroundTasks = tasks.Count > 0;
        });
    }

    private void OnChatServiceLoginRequired(string? errorMessage)
    {
        Dispatch(() =>
        {
            ActiveBanner = new LoginBannerViewModel(errorMessage, () =>
            {
                ActiveBanner = null;
                _chatService?.LaunchLogin();
            });
        });
    }

    private void OnPermissionBrokerRequested(PermissionRequest request)
    {
        // Read here, off the UI thread: the file can be large.
        var diff = ProposedFileDiff(request);

        Dispatch(() =>
        {
            try
            {
                _logger.LogInformation(
                    "[VM] Permission prompt requested (id={Id}, tool={Tool})",
                    request.Id, request.ToolName);
                ActiveBanner = new PermissionBannerViewModel(request, decision =>
                {
                    Dispatch(() => ActiveBanner = null);
                    _permissionBroker?.Resolve(request.Id, decision);
                }, diff, d => FileDiffRequested?.Invoke(d));
            }
            catch (Exception ex)
            {
                // Without this guard the throw escapes Dispatcher.BeginInvoke,
                // tears down the dispatcher loop, and leaves the chat hung.
                _logger.LogError(ex, "[VM] Permission prompt handler crashed (id={Id})", request.Id);
                ActiveBanner = null;
                try { _permissionBroker?.Resolve(request.Id, PermissionDecision.Deny("Banner failed to display")); }
                catch (Exception ex2) { _logger.LogError(ex2, "[VM] PermissionBroker.Resolve also failed"); }
            }
        });
    }

    /// <summary>
    /// The change a file edit waiting for permission would make, or null when
    /// it is not a file edit or would not apply.
    /// </summary>
    private FileDiffContent? ProposedFileDiff(PermissionRequest request)
    {
        try
        {
            return FileChanges.ForPermission(request.ToolName, request.Input,
                path => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null);
        }
        catch (Exception ex)
        {
            // The banner still works without the diff.
            _logger.LogWarning(ex, "[VM] Could not prepare the diff for {Tool}", request.ToolName);
            return null;
        }
    }

    private void OnQuestionBrokerRequested(UserQuestionRequest request)
    {
        Dispatch(() =>
        {
            try
            {
                _logger.LogInformation(
                    "[VM] User question requested (toolUseId={Id}, questions={Count})",
                    request.ToolUseId, request.Questions.Count);
                ActiveBanner = new QuestionCardViewModel(request, answers =>
                {
                    Dispatch(() => ActiveBanner = null);
                    _questionBroker?.Resolve(request.ToolUseId, answers);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VM] User question handler crashed (toolUseId={Id})", request.ToolUseId);
                ActiveBanner = null;
                try { _questionBroker?.Resolve(request.ToolUseId, new Dictionary<string, string>()); }
                catch (Exception ex2) { _logger.LogError(ex2, "[VM] QuestionBroker.Resolve also failed"); }
            }
        });
    }

    /// <summary>
    /// Stop answers every prompt in flight on the user's behalf, so the banner's
    /// own callback never runs and the banner would otherwise stay on screen
    /// long after its answer was sent. Take it down here, and say in the chat
    /// why it went away.
    /// </summary>
    private void OnPermissionCancelled(string requestId)
    {
        Dispatch(() =>
        {
            if (ActiveBanner is not PermissionBannerViewModel banner || banner.RequestId != requestId)
                return;

            _logger.LogInformation("[VM] Permission prompt cancelled (id={Id})", requestId);
            DismissActiveBanner($"_Stopped. The request to use **{banner.ToolName}** was denied._");
        });
    }

    private void OnQuestionCancelled(string toolUseId)
    {
        Dispatch(() =>
        {
            if (ActiveBanner is not QuestionCardViewModel card || card.ToolUseId != toolUseId)
                return;

            _logger.LogInformation("[VM] User question cancelled (toolUseId={Id})", toolUseId);
            DismissActiveBanner("_Stopped. The question was dismissed._");
        });
    }

    /// <summary>Clears the banner and leaves a row in the chat saying so. Must
    /// run on the UI thread.</summary>
    private void DismissActiveBanner(string notice)
    {
        ActiveBanner = null;
        UpdateActivityIndicator();

        var noticeId = $"dismissed-{++_userMsgCounter}";
        Items.Add(new ChatItemViewModel
        {
            Type = ChatItemType.Assistant,
            Content = notice,
            IsStreaming = false
        });
        MessageAdded?.Invoke(noticeId, ChatItemType.Assistant, new ChatMessageData
        {
            Id = noticeId,
            Type = "Assistant",
            Content = notice
        });
        RequestScroll();
    }

    /// <summary>
    /// Enables persistence for this chat session.
    /// </summary>
    public void EnablePersistence(ISessionStore sessionStore, string folderPath, int sessionId)
    {
        // Use reflection-free approach: store in mutable fields
        SetPersistence(sessionStore, folderPath, sessionId);
    }

    private ISessionStore? _sessionStore;
    private string? _folderPath;
    private int? _sessionId;

    private void SetPersistence(ISessionStore store, string folder, int id)
    {
        _sessionStore = store;
        _folderPath = folder;
        _sessionId = id;
    }

    private ISessionStore? ActiveStore => _sessionStore;
    private string? ActiveFolder => _folderPath;
    private int? ActiveSessionId => _sessionId;

    /// <summary>
    /// Restores previously saved messages into the Items collection and AI history.
    /// </summary>
    public async Task RestoreFromStoreAsync()
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;

        if (store is null || folder is null || !sessionId.HasValue) return;

        try
        {
            var messages = await store.GetMessagesAsync(folder, sessionId.Value);
            var restoreData = new List<ChatMessageData>();
            var msgIndex = 0;

            // Restored messages get ids of their own, so a subagent's step has
            // to find its Agent step's new id. The Agent step is saved after
            // its steps, hence the lookup over all messages first.
            var restoredIds = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].StepId is { } stepId)
                    restoredIds[stepId] = $"restore-{i}";
            }

            foreach (var msg in messages)
            {
                var type = ParseEnum<ChatItemType>(msg.ItemType);
                Items.Add(new ChatItemViewModel
                {
                    Type = type,
                    Content = msg.Content,
                    ToolName = msg.ToolName,
                    Title = msg.Title ?? "",
                    Body = msg.Body,
                    BodyMode = ParseEnum<OutputBodyMode>(msg.BodyMode ?? "Markdown"),
                    ExpanderTitle = msg.ExpanderTitle ?? "",
                    Status = ParseEnum<OutputItemStatus>(msg.StatusText),
                    IsStreaming = false
                });
                List<string>? imageDataUris = null;
                if (msg.ImageFileNames is { Count: > 0 })
                {
                    imageDataUris = new List<string>(msg.ImageFileNames.Count);
                    foreach (var fileName in msg.ImageFileNames)
                    {
                        // A missing file just means one thumbnail short; the rest
                        // of the conversation still restores. The same has to hold
                        // for a file that is present but cannot be read — locked
                        // by a virus scanner or OneDrive, denied, or a null entry
                        // in the list. Without a catch here the outer one takes
                        // over: no message is shown at all, and RestoreHistory is
                        // skipped, so the next message starts a new CLI session
                        // instead of continuing this one.
                        try
                        {
                            var stored = await store.GetImageAsync(folder, sessionId.Value, fileName);
                            if (stored is not null)
                                imageDataUris.Add(stored.ToDataUri());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[VM] Could not restore an attached image ({FileName})", fileName);
                        }
                    }
                }

                var restoreId = $"restore-{msgIndex++}";
                if (msg.FileChange is { } fileChange)
                    _fileChanges[restoreId] = fileChange;

                restoreData.Add(new ChatMessageData
                {
                    Id = restoreId,
                    HasFileChange = msg.FileChange is not null,
                    Type = type.ToString(),
                    Content = msg.Content,
                    ToolName = msg.ToolName,
                    Title = msg.Title ?? "",
                    Body = msg.Body,
                    BodyMode = msg.BodyMode ?? "Markdown",
                    ExpanderTitle = msg.ExpanderTitle ?? "",
                    Status = msg.StatusText,
                    IsStreaming = false,
                    Images = imageDataUris is { Count: > 0 } ? imageDataUris : null,
                    ParentId = msg.ParentId is { } parent && restoredIds.TryGetValue(parent, out var restoredParent)
                        ? restoredParent
                        : null
                });
            }
            if (restoreData.Count > 0)
                MessagesRestored?.Invoke(restoreData);

            var historyJson = await store.GetConversationHistoryAsync(folder, sessionId.Value);
            if (historyJson is not null && _chatService is not null)
            {
                _chatService.RestoreHistory(historyJson);

                // The CLI session id is only known now, so a model preview taken
                // at construction may have answered for a new session.
                RefreshModelPreview();
            }
        }
        catch
        {
            // Best effort — session works even if restore fails
        }
    }

    // An attachment on its own is a perfectly good prompt — "what is wrong
    // here?" is often implicit — so text is only required when nothing is
    // attached.
    private bool CanSend() =>
        !IsBusy && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0);

    /// <summary>
    /// Images and files pasted into the input box, waiting to go out with the
    /// next message. One list rather than two so the strip above the input keeps
    /// them in the order they were pasted.
    /// </summary>
    public ObservableCollection<IChatAttachment> PendingAttachments { get; } = new();

    /// <summary>
    /// Attaches an image or a file to the next message. Called by the host when
    /// the user pastes into the input box. Pasting the same path twice is a slip
    /// rather than an intent, so those are dropped; a screenshot has no path to
    /// compare and two identical ones are plausible enough to keep.
    /// </summary>
    public void Attach(IChatAttachment attachment)
    {
        if (attachment.SourcePath is { } path &&
            PendingAttachments.Any(
                a => string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        PendingAttachments.Add(attachment);
        SendCommand.NotifyCanExecuteChanged();

        // The file may be the one the editor suggests; it is attached now.
        RefreshEditorContext();
    }

    public void RemoveAttachment(IChatAttachment attachment)
    {
        PendingAttachments.Remove(attachment);
        SendCommand.NotifyCanExecuteChanged();

        // A removed editor context goes back to being a suggestion.
        if (attachment is EditorContext)
            RefreshEditorContext();
    }

    /// <summary>
    /// Puts the attached paths above the user's text, so the model knows what it
    /// has been handed before it reads the question about it. Images that came
    /// from a file are named here too, even though their bytes travel inline:
    /// without that, a paste of several photos arrives as thumbnails the model
    /// cannot tell apart, next to a list of the ones that did not fit inline.
    /// </summary>
    private static string WithFileReferences(string text, IReadOnlyList<IChatAttachment> attachments)
    {
        // Backticks stop Markdown from eating the backslashes when the message is
        // rendered back into the chat, and mark the path as literal for the CLI.
        var list = string.Join("\n", attachments.Select(a => $"- `{a.SourcePath}`"));
        var header = attachments.Count == 1 ? "Attached file:" : "Attached files:";

        return string.IsNullOrEmpty(text)
            ? $"{header}\n{list}"
            : $"{header}\n{list}\n\n{text}";
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var message = InputText.Trim();
        InputText = "";

        var images = PendingAttachments.OfType<ChatImageAttachment>().ToList();
        // In the order they were pasted, so the names line up with the
        // thumbnails the message carries.
        var named = PendingAttachments.Where(a => a.SourcePath is not null && a is not EditorContext).ToList();
        var editorContexts = PendingAttachments.OfType<EditorContext>().ToList();
        var sentImages = images.Count > 0 ? images : null;
        PendingAttachments.Clear();
        SendCommand.NotifyCanExecuteChanged();
        RefreshEditorContext();

        // Attached files are named in the prompt rather than uploaded: the CLI
        // reads them off disk with the same tools it uses for the rest of the
        // project. Listing them in the message itself also leaves a record in
        // the transcript of what went out.
        if (named.Count > 0)
            message = WithFileReferences(message, named);

        // Editor contexts go first, then the same way as files, except that the
        // CLI also gets the selected text.
        WithEditorContexts(editorContexts, ref message, out var sentMessage);

        // Written next to the session so reopening it shows the images again.
        var storedImageNames = sentImages is null
            ? null
            : await PersistImagesAsync(sentImages);

        var userMsgId = $"user-{++_userMsgCounter}";
        Items.Add(new ChatItemViewModel
        {
            Type = ChatItemType.User,
            Content = message,
            Title = "You"
        });
        MessageAdded?.Invoke(userMsgId, ChatItemType.User, new ChatMessageData
        {
            Id = userMsgId,
            Type = "User",
            Content = message,
            Title = "You",
            Images = sentImages?.Select(i => i.ToDataUri()).ToList()
        });
        RequestScroll();

        PersistMessageFireAndForget(new PersistedMessage
        {
            ItemType = ChatItemType.User.ToString(),
            Content = message,
            Title = "You",
            ImageFileNames = storedImageNames,
            CreatedUtc = DateTime.UtcNow
        });

        if (_chatService is null)
        {
            var errId = $"user-err-{_userMsgCounter}";
            var errContent = "_AI service not connected yet. This will be wired up in a future update._";
            Items.Add(new ChatItemViewModel
            {
                Type = ChatItemType.Assistant,
                Content = errContent,
                IsStreaming = false
            });
            MessageAdded?.Invoke(errId, ChatItemType.Assistant, new ChatMessageData
            {
                Id = errId,
                Type = "Assistant",
                Content = errContent
            });
            RequestScroll();
            return;
        }

        // Generate a title from the first user message that has text (fire-and-forget,
        // non-blocking). Skipped when the user already named the session by hand.
        // The title model only sees text: handed an image-only prompt it names the
        // session after the empty message ("Empty message, no request given"), so
        // such a session gets a placeholder until text arrives.
        var userMessages = Items.Where(i => i.Type == ChatItemType.User).ToList();
        var titleSource = WithoutEditorContextLines(message);
        var hasText = !string.IsNullOrWhiteSpace(titleSource);
        var isFirstMessage = userMessages.Count == 1;
        var isFirstText = hasText && userMessages.Count(i => !string.IsNullOrWhiteSpace(i.Content)) == 1;

        if (isFirstMessage && !hasText && !HasCustomTitle)
        {
            SessionTitle = PastedImageTitle;
            PersistTitleUpdateFireAndForget(PastedImageTitle);
        }
        else if (isFirstText && !HasCustomTitle)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var title = await _chatService.GenerateTitleAsync(titleSource);
                    Dispatch(() =>
                    {
                        // The user may have renamed the session while the
                        // title was being generated.
                        if (HasCustomTitle) return;

                        SessionTitle = title;
                        PersistTitleUpdateFireAndForget(title);
                    });
                }
                catch { /* best effort */ }
            });
        }

        IsBusy = true;
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;
        try
        {
            await foreach (var _ in _chatService.SendMessageAsync(sentMessage, sentImages, token))
            {
                // Output is handled by listener callbacks
            }

            OnExchangeCompleted();
        }
        catch (OperationCanceledException)
        {
            var cancelId = $"cancel-{++_userMsgCounter}";
            var cancelContent = "_Processing stopped._";
            Items.Add(new ChatItemViewModel
            {
                Type = ChatItemType.Assistant,
                Content = cancelContent,
                IsStreaming = false
            });
            MessageAdded?.Invoke(cancelId, ChatItemType.Assistant, new ChatMessageData
            {
                Id = cancelId,
                Type = "Assistant",
                Content = cancelContent
            });
        }
        catch (Exception ex)
        {
            var catchErrId = $"err-{++_userMsgCounter}";
            var catchErrContent = $"**Error:** {ex.Message}";
            Items.Add(new ChatItemViewModel
            {
                Type = ChatItemType.Assistant,
                Content = catchErrContent,
                IsStreaming = false
            });
            MessageAdded?.Invoke(catchErrId, ChatItemType.Assistant, new ChatMessageData
            {
                Id = catchErrId,
                Type = "Assistant",
                Content = catchErrContent
            });
        }
        finally
        {
            _sendCts?.Dispose();
            _sendCts = null;

            // A turn the CLI started on its own may already be running; it
            // keeps the session busy and ends the turn when it completes.
            if (!_inUnsolicitedTurn)
            {
                IsBusy = false;
                TurnEnded?.Invoke();
            }
        }
        RequestScroll();
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        try { _sendCts?.Cancel(); }
        catch { /* best effort — token may already be disposed */ }

        // Also with a sent message pending: its call interrupts the CLI only
        // while it still waits for its own result.
        if (_inUnsolicitedTurn)
        {
            try { _chatService?.StopUnsolicitedTurn(); }
            catch (Exception ex) { _logger.LogError(ex, "[VM] Stop: StopUnsolicitedTurn failed"); }
        }

        // The dispatcher loop blocks on the broker's TCS while a permission /
        // question banner is open. SendAsync's cancellation token doesn't reach
        // those TCSs (they're created with CancellationToken.None), so without
        // explicitly resolving them here a stuck banner leaves the chat hung
        // even after the user clicks Stop.
        //
        // Both calls raise PendingCancelled, which is what takes the banner off
        // the screen — see OnPermissionCancelled / OnQuestionCancelled.
        try { _questionBroker?.CancelAllPending(); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Stop: questionBroker.CancelAllPending failed"); }
        try { _permissionBroker?.CancelAllPending(); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Stop: permissionBroker.CancelAllPending failed"); }
    }

    [RelayCommand]
    private void Clear()
    {
        _chatService?.ClearHistory();
        Items.Clear();
        _activeItems.Clear();
        _fileChanges.Clear();
        AllCleared?.Invoke();
    }

    private void OnStepStarted(OutputItem item)
    {
        Dispatch(() =>
        {
            var isAi = item.ToolName == "AI";
            var isThinking = item.ToolName == "Thinking";
            var isAgent = item.ToolName == "Agent";

            var type = isAi ? ChatItemType.Assistant
                     : isThinking ? ChatItemType.Thinking
                     : ChatItemType.ToolStep;

            var streaming = isAi || isAgent || isThinking;
            var expanderTitle = isThinking ? "Thinking..." : item.Title;
            var vm = new ChatItemViewModel
            {
                Type = type,
                ToolName = item.ToolName,
                Title = item.Title,
                Status = item.Status,
                IsStreaming = streaming,
                ExpanderTitle = expanderTitle
            };
            _activeItems[item.Id] = vm;
            Items.Add(vm);
            MessageAdded?.Invoke(item.Id, type, new ChatMessageData
            {
                Id = item.Id,
                Type = type.ToString(),
                Content = "",
                ToolName = item.ToolName,
                Title = item.Title,
                Status = item.Status.ToString(),
                ExpanderTitle = expanderTitle,
                IsStreaming = streaming,
                // For a tool step this is the formatted input — the path being
                // read, the command being run. The folded row takes its first
                // line, which is what turns "Using Read" into something worth
                // reading while the task is still going.
                Body = type == ChatItemType.ToolStep ? item.Body : null,
                ParentId = item.ParentId
            });

            _currentStep = expanderTitle;
            _currentStepId = item.Id;
            UpdateBusyStatus();
            RequestScroll();
        });
    }

    private void OnStepUpdated(OutputItem item)
    {
        if (SessionLineSteps.Contains(item.ToolName))
        {
            Dispatch(() => RetitleNotice(item));
            return;
        }

        if (string.IsNullOrEmpty(item.Delta))
            return;

        Dispatch(() =>
        {
            if (_activeItems.TryGetValue(item.Id, out var vm))
            {
                vm.Content += item.Delta;

                if (item.ToolName == "Thinking")
                {
                    vm.ExpanderTitle = item.Title;
                    MessageStatusUpdated?.Invoke(item.Id, vm.Status, item.Title);
                }

                MessageContentUpdated?.Invoke(item.Id, vm.Content);

                var index = Items.IndexOf(vm);
                if (index >= 0 && index < Items.Count - 1)
                {
                    Items.Move(index, Items.Count - 1);
                }
            }
        });
    }

    /// <summary>
    /// A notice line says what it is in its title, and the title moves on
    /// while the line is open: the retry count, a compaction that finished.
    /// The bar under the chat follows it.
    /// </summary>
    private void RetitleNotice(OutputItem item)
    {
        if (!_activeItems.TryGetValue(item.Id, out var vm)) return;

        vm.ExpanderTitle = item.Title;
        MessageStatusUpdated?.Invoke(item.Id, vm.Status, item.Title);

        if (_currentStepId == item.Id)
        {
            _currentStep = item.Title;
            UpdateBusyStatus();
        }
    }

    private void OnStepCompleted(OutputItem item)
    {
        Dispatch(() =>
        {
            if (_activeItems.TryGetValue(item.Id, out var vm))
            {
                if (SessionLineSteps.Contains(item.ToolName))
                    RetitleNotice(item);

                vm.Status = item.Status;
                vm.IsStreaming = false;

                MessageStatusUpdated?.Invoke(item.Id, item.Status,
                    item.ToolName == "Thinking" ? item.Title : vm.ExpanderTitle);

                if (item.ToolName == "Thinking")
                {
                    vm.ExpanderTitle = item.Title;
                }
                else if (!string.IsNullOrEmpty(item.Body) && item.ToolName != "AI")
                {
                    vm.Body = item.Body;
                    vm.BodyMode = item.BodyMode;
                    MessageBodySet?.Invoke(item.Id, item.Body!, item.BodyMode);
                }

                MessageCompleted?.Invoke(item.Id);

                // Back to the plain "Thinking..." until the next step names
                // itself — but only if this is the step the bar is showing.
                if (_currentStepId == item.Id)
                {
                    _currentStep = null;
                    _currentStepId = null;
                    UpdateBusyStatus();
                }

                _activeItems.TryRemove(item.Id, out _);
                RequestScroll();

                // Persist completed step
                PersistMessageFireAndForget(new PersistedMessage
                {
                    ItemType = vm.Type.ToString(),
                    Content = vm.Content,
                    ToolName = vm.ToolName,
                    Title = vm.Title,
                    Body = vm.Body,
                    BodyMode = vm.BodyMode.ToString(),
                    ExpanderTitle = vm.ExpanderTitle,
                    StatusText = vm.Status.ToString(),
                    StepId = item.Id,
                    ParentId = item.ParentId,
                    FileChange = _fileChanges.TryGetValue(item.Id, out var fileChange) ? fileChange : null,
                    CreatedUtc = DateTime.UtcNow
                });
            }
        });
    }

    // --- Persistence helpers (fire-and-forget) ---

    /// <summary>
    /// Writes attachments into the session folder before the message that
    /// references them is appended, so a reopened session never points at files
    /// that are not there yet. Awaited rather than fire-and-forget for that
    /// reason. Returns null when persistence is off or every write failed.
    /// </summary>
    private async Task<List<string>?> PersistImagesAsync(IReadOnlyList<ChatImageAttachment> images)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return null;

        var names = new List<string>(images.Count);
        foreach (var image in images)
        {
            try
            {
                names.Add(await store.SaveImageAsync(folder, sessionId.Value, image));
            }
            catch (Exception ex)
            {
                // The message still goes out with the image inline; only the
                // stored copy is lost, so this must not abort the send.
                _logger.LogWarning(ex, "[VM] Could not store attached image");
            }
        }

        return names.Count > 0 ? names : null;
    }

    private void PersistMessageFireAndForget(PersistedMessage message)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return;

        EnqueueMessageWrite(() => store.AppendMessageAsync(folder, sessionId.Value, message));
    }

    private void PersistStepFileChangeFireAndForget(string stepId, FileChange change)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return;

        EnqueueMessageWrite(() => store.SetStepFileChangeAsync(folder, sessionId.Value, stepId, change));
    }

    // Writes to messages.json run one after another, in the order they were
    // asked for: a file change for a closed step has to find the step saved.
    private readonly object _messageWriteLock = new();
    private Task _messageWriteTail = Task.CompletedTask;

    private void EnqueueMessageWrite(Func<Task> write)
    {
        lock (_messageWriteLock)
        {
            _messageWriteTail = _messageWriteTail.ContinueWith(async _ =>
            {
                try { await write(); }
                catch { /* best effort */ }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private void PersistConversationHistoryFireAndForget()
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue || _chatService is null) return;

        var historyJson = _chatService.SerializeHistory();
        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveConversationHistoryAsync(folder, sessionId.Value, historyJson);

                var index = await store.GetSessionIndexAsync(folder);
                var entry = index.FirstOrDefault(e => e.Id == sessionId.Value);
                if (entry is not null)
                {
                    entry.LastActivityUtc = DateTime.UtcNow;
                    await store.UpdateSessionAsync(folder, entry);
                }
            }
            catch { /* best effort */ }
        });
    }

    private void PersistTitleUpdateFireAndForget(string title)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var index = await store.GetSessionIndexAsync(folder);
                var entry = index.FirstOrDefault(e => e.Id == sessionId.Value);
                if (entry is not null)
                {
                    entry.Title = title;
                    entry.LastActivityUtc = DateTime.UtcNow;
                    await store.UpdateSessionAsync(folder, entry);
                }
            }
            catch { /* best effort */ }
        });
    }

    private void RequestScroll() => ScrollRequested?.Invoke();

    private static T ParseEnum<T>(string value) where T : struct
        => Enum.TryParse<T>(value, ignoreCase: true, out var result) ? result : default;

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Sets a disposable scope (typically the DI <c>ServiceProvider</c>) that
    /// will be disposed when this view model is disposed, cascading disposal to
    /// the <c>ClaudeCliChatService</c> → <c>ClaudeCliProcessHost</c> (kills the
    /// child process and tears down the permission pipe).
    /// </summary>
    public void SetServiceScope(IDisposable scope) => _serviceScope = scope;

    public void Dispose()
    {
        try { _activityTimer?.Stop(); } catch { }
        try { EditorContextProvider = null; } catch { }
        try
        {
            if (_chatService is not null)
            {
                _chatService.UsageChanged -= OnChatServiceUsageChanged;
                _chatService.ModelChanged -= OnChatServiceModelChanged;
                _chatService.UnsolicitedTurnStarted -= OnUnsolicitedTurnStarted;
                _chatService.UnsolicitedTurnCompleted -= OnUnsolicitedTurnCompleted;
                _chatService.AppliedEffortChanged -= OnChatServiceAppliedEffortChanged;
                _chatService.FastModeChanged -= OnChatServiceFastModeChanged;
                _chatService.BackgroundTasksChanged -= OnChatServiceBackgroundTasksChanged;
                _chatService.StepBackgroundStateChanged -= OnChatServiceStepBackgroundStateChanged;
                _chatService.StepCanMoveToBackground -= OnChatServiceStepCanMoveToBackground;
                _chatService.StepFileChanged -= OnChatServiceStepFileChanged;
            }
            // Static: without this, a closed session stays reachable from the catalog.
            ClaudeModelCatalog.ReportedChanged -= OnReportedModelsChanged;
        }
        catch { }
        try { (_chatService as IDisposable)?.Dispose(); } catch { }
        try { _serviceScope?.Dispose(); } catch { }
    }
}

public enum SessionActivity
{
    Idle,
    Busy,
    AwaitingUser,
    Completed
}
