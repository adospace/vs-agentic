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

    // Realtime activity indicator: a braille spinner prefix while the AI is
    // working, a blinking hand while awaiting user input (permission /
    // question banner), and no prefix while idle. Host bindings (e.g. the VS
    // tool window caption) should use DisplayTitle; SessionTitle stays plain
    // for the session list entry so the sidebar doesn't flicker.
    private static readonly string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    // Two hand glyphs rather than a hand alternating with blank: the caption is
    // a plain string with no way to hide a glyph, so anything but an equal-width
    // pair shifts the title text on every frame. The trailing U+FE0F on the
    // raised hand forces emoji presentation — its text-presentation fallback is
    // narrower, which would reintroduce the shift.
    private static readonly string[] AwaitingFrames = { "👋 ", "✋️ " };
    // The timer runs at spinner speed, which would strobe the hand, so the hand
    // advances only every Nth tick (~0.5s). The tick counter wraps at the LCM of
    // both cycles (10 spinner frames, 2 x 4 hand ticks) so neither jumps on rollover.
    private const int AwaitingTicksPerFrame = 4;
    private const int AnimationTickCycle = 40;
    private int _pendingUserPrompts;
    private int _animationTick;
    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    [ObservableProperty]
    private string _displayTitle = "New Session";

    public SessionActivity Activity =>
        _pendingUserPrompts > 0 ? SessionActivity.AwaitingUser :
        IsBusy ? SessionActivity.Busy :
        SessionActivity.Idle;

    partial void OnIsBusyChanged(bool value) => UpdateActivityIndicator();

    partial void OnSessionTitleChanged(string value) => UpdateDisplayTitle();

    private void UpdateActivityIndicator()
    {
        // Activity is computed, so it has to be notified by hand for the
        // status-bar triggers in ChatSessionControl.xaml to see the change.
        OnPropertyChanged(nameof(Activity));

        if (Activity is SessionActivity.Busy or SessionActivity.AwaitingUser)
        {
            EnsureActivityTimer();
            if (!_activityTimer!.IsEnabled) _activityTimer.Start();
        }
        else
        {
            _activityTimer?.Stop();
            _animationTick = 0;
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
            _animationTick = (_animationTick + 1) % AnimationTickCycle;
            UpdateDisplayTitle();
        };
    }

    private void UpdateDisplayTitle()
    {
        var prefix = Activity switch
        {
            SessionActivity.Busy => SpinnerFrames[_animationTick % SpinnerFrames.Length] + " ",
            SessionActivity.AwaitingUser =>
                AwaitingFrames[(_animationTick / AwaitingTicksPerFrame) % AwaitingFrames.Length],
            _ => ""
        };
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
        WorkingDirectory = options.Value.WorkingDirectory;
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
        chatService.ModelChanged += OnChatServiceModelChanged;
        StatusInfo = FormatModelName(chatService.CurrentModel);
        StartModelProbe();
    }

    private void OnChatServiceModelChanged(string? model)
        => Dispatch(() => StatusInfo = FormatModelName(model));

    private int _modelProbeGeneration;

    /// <summary>
    /// Fills the status strip with this session's model at window load, since the
    /// CLI's authoritative <c>system/init</c> value doesn't arrive until the first
    /// message is sent.
    ///
    /// Which source is correct depends on whether the session is new: <c>--resume</c>
    /// keeps a session on the model it was created with, so the configured default
    /// is only valid for a session that hasn't run yet. Called again after history
    /// is restored, when the session id becomes known.
    ///
    /// Runs off the UI thread (it reads settings files and possibly a transcript).
    /// </summary>
    private void StartModelProbe()
    {
        var generation = Interlocked.Increment(ref _modelProbeGeneration);

        Task.Run(() =>
        {
            string? probed;
            try
            {
                // A known session id means this session has already run, so ask
                // what it actually ran on. Only fall back to the configured
                // default when there's no transcript to consult — without one the
                // CLI can't carry a model forward either.
                var sessionId = _chatService?.CliSessionId;
                probed = ClaudeModelResolver.ResolveSessionModel(sessionId, _logger)
                    ?? ClaudeModelResolver.ResolveConfiguredModel(WorkingDirectory, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ChatSession] Model probe failed");
                return;
            }

            if (probed is null) return;

            Dispatch(() =>
            {
                // Don't overwrite a better answer: the CLI's real value, or a
                // later probe that had the session id this one lacked.
                if (_chatService?.CurrentModel is not null) return;
                if (Volatile.Read(ref _modelProbeGeneration) != generation) return;
                StatusInfo = FormatModelName(probed);
            });
        });
    }

    /// <summary>
    /// Turns the CLI's model id into something short enough for the status strip:
    /// "claude-opus-5" → "Opus 5", "claude-3-5-haiku-20241022" → "Haiku 3.5".
    /// Unrecognized ids are shown verbatim minus the "claude-" prefix, so a new
    /// model still displays something sensible without a code change.
    /// </summary>
    internal static string FormatModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";

        var id = model!.Trim();

        // "claude-fable-5[1m]" — the 1M-context variant. Strip the marker so the
        // version parsing below works, and re-attach it to the display name.
        var suffix = "";
        var bracket = id.IndexOf('[');
        if (bracket > 0 && id.EndsWith("]", StringComparison.Ordinal))
        {
            suffix = " " + id.Substring(bracket + 1, id.Length - bracket - 2).ToUpperInvariant();
            id = id.Substring(0, bracket);
        }

        if (id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            id = id.Substring("claude-".Length);

        // Drop a trailing yyyyMMdd date stamp ("haiku-4-5-20251001" → "haiku-4-5").
        var parts = id.Split('-').ToList();
        var last = parts[parts.Count - 1];
        if (parts.Count > 1 && last.Length == 8 && last.All(char.IsDigit))
            parts.RemoveAt(parts.Count - 1);

        // Family name plus whatever version segments surround it, in either the
        // "opus-5" or legacy "3-5-haiku" ordering.
        var familyIndex = parts.FindIndex(p =>
            p.Equals("opus", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("sonnet", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("haiku", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("fable", StringComparison.OrdinalIgnoreCase));
        if (familyIndex < 0)
            return "Model: " + model;

        var family = parts[familyIndex];
        family = char.ToUpperInvariant(family[0]) + family.Substring(1).ToLowerInvariant();

        var version = string.Join(".", parts.Where((p, i) => i != familyIndex && p.All(char.IsDigit)));
        return string.IsNullOrEmpty(version)
            ? "Model: " + family + suffix
            : "Model: " + family + " " + version + suffix;
    }

    /// <summary>
    /// Text shown in the status strip beside the @-mention button. Currently the
    /// active model; plan/usage details are intended to join it here.
    /// </summary>
    [ObservableProperty]
    private string _statusInfo = "";

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
                },
                onRememberAllow: remembered => _permissionBroker?.RememberAllow(remembered));
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
        // The broker is a singleton spanning the whole extension lifetime, so
        // "allow for the rest of the session" grants have to be dropped
        // explicitly when we move to a different chat session.
        if (_sessionId != id)
            _permissionBroker?.ClearRememberedAllows();

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

                // The session id is only known now. Re-probe so a restored session
                // shows the model it actually ran on rather than the current
                // default — unless RestoreHistory already supplied one.
                if (_chatService.CurrentModel is null)
                    StartModelProbe();
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

        // Generate a title from the first user message (fire-and-forget, non-blocking)
        var isFirstMessage = Items.Count(i => i.Type == ChatItemType.User) == 1;
        if (isFirstMessage)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var title = await _chatService.GenerateTitleAsync(message);
                    Dispatch(() =>
                    {
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
        var isThinking = item.ToolName == "Thinking";

        // Thinking updates are driven by the CLI's thinking_tokens events, which carry
        // a live title but no text (the thinking block itself comes back redacted), so
        // they must not be gated on Delta.
        if (string.IsNullOrEmpty(item.Delta) && !isThinking)
            return;

        Dispatch(() =>
        {
            if (_activeItems.TryGetValue(item.Id, out var vm))
            {
                vm.Content += item.Delta;

                if (isThinking)
                {
                    vm.ExpanderTitle = item.Title;
                    MessageStatusUpdated?.Invoke(item.Id, vm.Status, item.Title);
                }

                if (!string.IsNullOrEmpty(item.Delta))
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
        if (_chatService is not null)
        {
            _chatService.LoginRequired -= OnChatServiceLoginRequired;
            _chatService.ModelChanged -= OnChatServiceModelChanged;
        }
        try { (_chatService as IDisposable)?.Dispose(); } catch { }
        try { _serviceScope?.Dispose(); } catch { }
    }
}

public enum SessionActivity
{
    Idle,
    Busy,
    AwaitingUser
}
