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

    /// <summary>
    /// Title of a session whose first message is an image with no text, until
    /// a message with text gives the title model something to summarize.
    /// </summary>
    private const string PastedImageTitle = "Pasted image";

    // Realtime activity indicator: a braille spinner prefix while the AI is
    // working, a steady "? " prefix while awaiting user input (permission /
    // question banner), and no prefix while idle. Host bindings (e.g. the VS
    // tool window caption) should use DisplayTitle; SessionTitle stays plain
    // for the session list entry so the sidebar doesn't flicker.
    private static readonly string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private const string AwaitingPrefix = "? ";
    private int _spinnerFrame;
    private System.Windows.Threading.DispatcherTimer? _activityTimer;

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
        SessionActivity.Idle;

    partial void OnIsBusyChanged(bool value)
    {
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
        // Driven by IsBusy rather than Activity: the clock has to keep running
        // while a permission or question banner is up, which is exactly when
        // the user most wants to know how long this has been going on.
        if (IsBusy)
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
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
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
        var prefix = Activity switch
        {
            SessionActivity.Busy => SpinnerFrames[_spinnerFrame] + " ",
            SessionActivity.AwaitingUser => AwaitingPrefix,
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

    // ── Background tasks ──────────────────────────────────────────────────
    // Commands the CLI runs with run_in_background. They outlive the turn that
    // started them, so the strip above the input box is the only place that
    // shows they are still running.

    [ObservableProperty]
    private bool _hasBackgroundTasks;

    [ObservableProperty]
    private string _backgroundTasksText = "";

    [ObservableProperty]
    private string _backgroundTasksTooltip = "";

    private void OnChatServiceBackgroundTasksChanged(IReadOnlyList<BackgroundTask> tasks)
    {
        Dispatch(() =>
        {
            HasBackgroundTasks = tasks.Count > 0;
            BackgroundTasksText = tasks.Count switch
            {
                0 => "",
                1 => "Running in background: " + tasks[0].Description,
                _ => $"{tasks.Count} tasks running in background",
            };
            BackgroundTasksTooltip = string.Join("\n", tasks.Select(t => "• " + t.Description));
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
                });
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
                    IsStreaming = false,
                    Images = imageDataUris is { Count: > 0 } ? imageDataUris : null
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
    }

    public void RemoveAttachment(IChatAttachment attachment)
    {
        PendingAttachments.Remove(attachment);
        SendCommand.NotifyCanExecuteChanged();
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
        var named = PendingAttachments.Where(a => a.SourcePath is not null).ToList();
        var sentImages = images.Count > 0 ? images : null;
        PendingAttachments.Clear();
        SendCommand.NotifyCanExecuteChanged();

        // Attached files are named in the prompt rather than uploaded: the CLI
        // reads them off disk with the same tools it uses for the rest of the
        // project. Listing them in the message itself also leaves a record in
        // the transcript of what went out.
        if (named.Count > 0)
            message = WithFileReferences(message, named);

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
        var hasText = !string.IsNullOrWhiteSpace(message);
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
            await foreach (var _ in _chatService.SendMessageAsync(message, sentImages, token))
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
                Body = type == ChatItemType.ToolStep ? item.Body : null
            });

            _currentStep = expanderTitle;
            _currentStepId = item.Id;
            UpdateBusyStatus();
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
                _chatService.UnsolicitedTurnStarted -= OnUnsolicitedTurnStarted;
                _chatService.UnsolicitedTurnCompleted -= OnUnsolicitedTurnCompleted;
                _chatService.BackgroundTasksChanged -= OnChatServiceBackgroundTasksChanged;
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
    AwaitingUser
}
