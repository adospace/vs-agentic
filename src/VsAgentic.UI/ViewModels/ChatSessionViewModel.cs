using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.Abstractions;
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

    private int _pendingUserPrompts;
    private int _tick;
    private SessionActivity _lastActivity = SessionActivity.Idle;
    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    // Set when a turn ends unwatched, and cleared the instant the window is
    // looked at. A separate flag rather than folding into Activity's other
    // inputs because, unlike Busy/_pendingUserPrompts, nothing about the
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

    public SessionActivity Activity =>
        _pendingUserPrompts > 0 ? SessionActivity.AwaitingUser :
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
    public string StatusText => Activity switch
    {
        SessionActivity.Busy => "Thinking...",
        SessionActivity.AwaitingUser => "Waiting for input…",
        SessionActivity.Completed => "Done",
        _ => ""
    };

    /// <summary>Whether the status bar should be pulsing. Bound by ChatSessionControl.xaml.</summary>
    public bool IsFlashing =>
        (Activity == SessionActivity.AwaitingUser && _options.FlashStatusBarWhileWaiting)
        || (Activity == SessionActivity.Completed && _options.FlashStatusBarWhenComplete);

    partial void OnIsBusyChanged(bool value) => UpdateActivityIndicator();

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

        if (CurrentAnimation is not null)
        {
            EnsureActivityTimer();
            if (!_activityTimer!.IsEnabled) _activityTimer.Start();
        }
        else
        {
            _activityTimer?.Stop();
        }
        UpdateDisplayTitle();
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
            if (animation is null)
            {
                _activityTimer!.Stop();
                return;
            }

            // A held animation wraps so the counter stays bounded; a transient
            // one must keep counting up or it can never reach its duration.
            _tick = animation.DurationTicks is null
                ? (_tick + 1) % animation.CycleTicks
                : _tick + 1;

            if (animation.HasExpired(_tick))
                _activityTimer!.Stop();

            UpdateDisplayTitle();
        };
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
    public event Action<string>? MessageCompleted;
    public event Action? AllCleared;
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
            _permissionBroker.PermissionRequested += OnPermissionBrokerRequested;
        if (_questionBroker is not null)
            _questionBroker.QuestionRequested += OnQuestionBrokerRequested;

        chatService.LoginRequired += OnChatServiceLoginRequired;

        InitializeUsage(chatService, options.Value);
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
        Dispatch(() =>
        {
            try
            {
                _pendingUserPrompts++;
                UpdateActivityIndicator();
                _logger.LogInformation(
                    "[VM] Permission prompt requested (id={Id}, tool={Tool})",
                    request.Id, request.ToolName);
                ActiveBanner = new PermissionBannerViewModel(request, decision =>
                {
                    Dispatch(() =>
                    {
                        ActiveBanner = null;
                        if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                        UpdateActivityIndicator();
                    });
                    _permissionBroker?.Resolve(request.Id, decision);
                });
            }
            catch (Exception ex)
            {
                // Without this guard the throw escapes Dispatcher.BeginInvoke,
                // tears down the dispatcher loop, and leaves the chat hung.
                _logger.LogError(ex, "[VM] Permission prompt handler crashed (id={Id})", request.Id);
                if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                UpdateActivityIndicator();
                try { _permissionBroker?.Resolve(request.Id, PermissionDecision.Deny("Banner failed to display")); }
                catch (Exception ex2) { _logger.LogError(ex2, "[VM] PermissionBroker.Resolve also failed"); }
            }
        });
    }

    private void OnQuestionBrokerRequested(UserQuestionRequest request)
    {
        Dispatch(() =>
        {
            try
            {
                _pendingUserPrompts++;
                UpdateActivityIndicator();
                _logger.LogInformation(
                    "[VM] User question requested (toolUseId={Id}, questions={Count})",
                    request.ToolUseId, request.Questions.Count);
                ActiveBanner = new QuestionCardViewModel(request, answers =>
                {
                    Dispatch(() =>
                    {
                        ActiveBanner = null;
                        if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                        UpdateActivityIndicator();
                    });
                    _questionBroker?.Resolve(request.ToolUseId, answers);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VM] User question handler crashed (toolUseId={Id})", request.ToolUseId);
                if (_pendingUserPrompts > 0) _pendingUserPrompts--;
                UpdateActivityIndicator();
                try { _questionBroker?.Resolve(request.ToolUseId, new Dictionary<string, string>()); }
                catch (Exception ex2) { _logger.LogError(ex2, "[VM] QuestionBroker.Resolve also failed"); }
            }
        });
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
                restoreData.Add(new ChatMessageData
                {
                    Id = $"restore-{msgIndex++}",
                    Type = type.ToString(),
                    Content = msg.Content,
                    ToolName = msg.ToolName,
                    Title = msg.Title ?? "",
                    Body = msg.Body,
                    BodyMode = msg.BodyMode ?? "Markdown",
                    ExpanderTitle = msg.ExpanderTitle ?? "",
                    Status = msg.StatusText,
                    IsStreaming = false
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

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var message = InputText.Trim();
        InputText = "";

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
            Title = "You"
        });
        RequestScroll();

        PersistMessageFireAndForget(new PersistedMessage
        {
            ItemType = ChatItemType.User.ToString(),
            Content = message,
            Title = "You",
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

        // Generate a title from the first user message (fire-and-forget, non-blocking).
        // Skipped when the user already named the session by hand.
        var isFirstMessage = Items.Count(i => i.Type == ChatItemType.User) == 1;
        if (isFirstMessage && !HasCustomTitle)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var title = await _chatService.GenerateTitleAsync(message);
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
            await foreach (var _ in _chatService.SendMessageAsync(message, token))
            {
                // Output is handled by listener callbacks
            }

            // Persist conversation history after each completed exchange
            PersistConversationHistoryFireAndForget();

            // Refresh cost and last activity in the session list
            if (_chatService is not null && SessionInfo is not null)
            {
                SessionInfo.SessionCost = _chatService.GetSessionCost();
                SessionInfo.LastActivity = DateTime.Now;
            }
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
            // Every turn end counts, not just clean ones — Stop and an error
            // both still leave the window worth glancing back at. Unless the
            // user is already in it, in which case they have just watched the
            // turn end and there is nothing left to announce.
            _turnUnseen = !IsSessionFocused;
            IsBusy = false;
        }
        RequestScroll();
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        try { _sendCts?.Cancel(); }
        catch { /* best effort — token may already be disposed */ }

        // The dispatcher loop blocks on the broker's TCS while a permission /
        // question banner is open. SendAsync's cancellation token doesn't reach
        // those TCSs (they're created with CancellationToken.None), so without
        // explicitly resolving them here a stuck banner leaves the chat hung
        // even after the user clicks Stop.
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
                IsStreaming = streaming
            });
            RequestScroll();
        });
    }

    private void OnStepUpdated(OutputItem item)
    {
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

    private void OnStepCompleted(OutputItem item)
    {
        Dispatch(() =>
        {
            if (_activeItems.TryGetValue(item.Id, out var vm))
            {
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
                    CreatedUtc = DateTime.UtcNow
                });
            }
        });
    }

    // --- Persistence helpers (fire-and-forget) ---

    private void PersistMessageFireAndForget(PersistedMessage message)
    {
        var store = ActiveStore;
        var folder = ActiveFolder;
        var sessionId = ActiveSessionId;
        if (store is null || folder is null || !sessionId.HasValue) return;

        _ = Task.Run(async () =>
        {
            try { await store.AppendMessageAsync(folder, sessionId.Value, message); }
            catch { /* best effort */ }
        });
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
        try
        {
            if (_chatService is not null)
            {
                _chatService.UsageChanged -= OnChatServiceUsageChanged;
                _chatService.ModelChanged -= OnChatServiceModelChanged;
            }
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
