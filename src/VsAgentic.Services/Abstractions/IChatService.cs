using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.Abstractions;

public interface IChatService
{
    /// <param name="images">
    /// Images to send alongside the text. They travel inline as base64 blocks,
    /// so nothing is written to the project. Null or empty sends text only,
    /// which keeps the wire format identical to what it was before images
    /// existed.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the turn. The CLI is told to abort it too, and the call throws
    /// <see cref="OperationCanceledException"/> once the CLI has closed the
    /// turn, or after a short wait if it does not.
    /// </param>
    IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        IReadOnlyList<ChatImageAttachment>? images = null,
        CancellationToken cancellationToken = default);
    Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default);
    void ClearHistory();

    /// <summary>
    /// Serializes the current conversation history to a JSON string for persistence.
    /// </summary>
    string SerializeHistory();

    /// <summary>
    /// Restores conversation history from a previously serialized JSON string.
    /// </summary>
    void RestoreHistory(string serializedHistory);

    /// <summary>
    /// Returns the cumulative USD cost for this session based on CLI cost reporting.
    /// Returns null when no messages have been sent yet.
    /// </summary>
    decimal? GetSessionCost();

    /// <summary>
    /// The model the CLI reported for the current session (e.g. "claude-opus-5[1m]"),
    /// taken from the <c>system/init</c> event, or restored with the session
    /// history. Null until either has happened — the CLI emits init with the
    /// first message, not at process start.
    /// </summary>
    string? CurrentModel { get; }

    /// <summary>
    /// The CLI's own session id, once one exists — either restored from persisted
    /// history or assigned when the session started. Lets the host locate the
    /// CLI's transcript for this session.
    /// </summary>
    string? CliSessionId { get; }

    /// <summary>
    /// Raised when <see cref="CurrentModel"/> changes — on session start, on
    /// restore, and again after a process restart, which can pick up a different
    /// model. Raised on a background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<string?>? ModelChanged;

    /// <summary>
    /// Token usage for this session, plus the rolling machine-wide windows, as
    /// of now. Never null — a session that has sent nothing reports zeroes.
    /// </summary>
    SessionUsage GetUsage();

    /// <summary>
    /// Raised after each API call the CLI reports usage for, carrying the same
    /// snapshot <see cref="GetUsage"/> would return. Fires on a background
    /// thread; hosts must marshal to their UI thread.
    /// </summary>
    event Action<SessionUsage>? UsageChanged;

    /// <summary>
    /// Switches the model and/or reasoning effort for this session.
    ///
    /// A running process takes the change at once and applies it from its next
    /// API request, also within a running turn. A process started later gets
    /// it as start-up flags. If the running process cannot take it, the next
    /// <see cref="SendMessageAsync"/> restarts the process and resumes the same
    /// conversation. A no-op when neither value changed.
    /// </summary>
    void ApplyModelAndEffort(string modelAlias, ClaudeEffort effort);

    /// <summary>
    /// The effort level the CLI will send on its next request, as it reports
    /// it — what <see cref="ClaudeEffort.Default"/> resolves to, or the chosen
    /// level after the CLI lowered it for a model that does not support it.
    /// Null when not known yet, and when the model has no effort setting.
    /// </summary>
    ClaudeEffort? AppliedEffort { get; }

    /// <summary>
    /// Raised when <see cref="AppliedEffort"/> changes. Raised on a background
    /// thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<ClaudeEffort?>? AppliedEffortChanged;

    /// <summary>
    /// Asks for fast mode for the rest of this session, or stops asking. A
    /// running process takes it from its next request; a process started later
    /// gets it before the first message. Not stored anywhere: a new session
    /// starts without it.
    /// </summary>
    void SetFastMode(bool on);

    /// <summary>Whether the user asked for fast mode in this session.</summary>
    bool FastModeRequested { get; }

    /// <summary>
    /// Fast mode as the CLI last reported it. Whether a request runs fast
    /// depends on this, not on <see cref="FastModeRequested"/>: the model, the
    /// plan or the account can keep it off.
    /// </summary>
    FastModeStatus FastMode { get; }

    /// <summary>
    /// Raised when <see cref="FastMode"/> changes. Raised on a background
    /// thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<FastModeStatus>? FastModeChanged;

    /// <summary>
    /// Raised when the underlying CLI returned an authentication / login-required
    /// error. The string argument is the original error text from the CLI so the
    /// host can surface it to the user. Hosts should respond by showing a login
    /// banner and calling <see cref="LaunchLogin"/> when the user opts in.
    /// </summary>
    event Action<string?>? LoginRequired;

    /// <summary>
    /// Launches an interactive Claude CLI window so the user can complete the
    /// OAuth / login flow, and tears down the current long-running CLI process
    /// so the next <see cref="SendMessageAsync"/> call starts fresh against the
    /// new credentials.
    /// </summary>
    void LaunchLogin();

    /// <summary>
    /// Raised when the CLI starts a turn without a <see cref="SendMessageAsync"/>
    /// call, for example when a background command finishes and wakes the
    /// model. The turn's output arrives through the output listener as usual,
    /// also while a sent message waits in the CLI's queue behind the turn.
    /// Raised on a background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action? UnsolicitedTurnStarted;

    /// <summary>
    /// Raised when a turn announced by <see cref="UnsolicitedTurnStarted"/>
    /// ends, is stopped, or is cut off by the process exiting. Also raised
    /// when the CLI takes a queued message into the turn: from then on the
    /// turn's output belongs to that message. Raised on a background thread.
    /// </summary>
    event Action? UnsolicitedTurnCompleted;

    /// <summary>
    /// Stops the turn the CLI started on its own. Like stopping a
    /// <see cref="SendMessageAsync"/> call, this interrupts the turn in the
    /// CLI; <see cref="UnsolicitedTurnCompleted"/> follows when the CLI has
    /// closed it. Returns at once. A no-op when no such turn is running.
    /// </summary>
    void StopUnsolicitedTurn();

    /// <summary>
    /// Raised when the work a tool step moved to the background starts or
    /// ends. Arguments: the step id (the same id the output listener used for
    /// the step), the new state, the CLI's summary when the work ended,
    /// e.g. <c>Background command "…" failed with exit code 3</c>, and the
    /// file the CLI wrote a shell command's output to (null for other work).
    /// Can fire between turns. Raised on a background thread; hosts must
    /// marshal to the UI thread.
    /// </summary>
    event Action<string, BackgroundStepState, string?, string?>? StepBackgroundStateChanged;

    /// <summary>
    /// Raised when a step's command or subagent, still running in the
    /// foreground, can be moved to the background with
    /// <see cref="MoveStepToBackgroundAsync"/>. The CLI reports this a few
    /// seconds after the tool call starts. The argument is the step id.
    /// Raised on a background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<string>? StepCanMoveToBackground;

    /// <summary>
    /// Moves the command or subagent of a running step to the background. The
    /// tool call returns at once and the turn goes on. Returns false when the
    /// step no longer runs in the foreground, or when no CLI process runs.
    /// </summary>
    Task<bool> MoveStepToBackgroundAsync(string stepId);

    /// <summary>
    /// Stops one of the <see cref="BackgroundTasks"/>. The other tasks and the
    /// turn, if one runs, go on. Returns false when the CLI did not take the
    /// request.
    /// </summary>
    Task<bool> StopBackgroundTaskAsync(string taskId);

    /// <summary>
    /// The end of what a background shell task has written so far. Null when
    /// the CLI could not say, e.g. for a subagent task.
    /// </summary>
    Task<BackgroundTaskOutput?> GetBackgroundTaskOutputAsync(string taskId);

    /// <summary>
    /// The tasks the CLI is running in the background right now. Empty when
    /// none run, and after the CLI process stops, which ends them.
    /// </summary>
    IReadOnlyList<BackgroundTask> BackgroundTasks { get; }

    /// <summary>
    /// Raised when <see cref="BackgroundTasks"/> changes, carrying the new list.
    /// Can fire between turns. Raised on a background thread; hosts must
    /// marshal to the UI thread.
    /// </summary>
    event Action<IReadOnlyList<BackgroundTask>>? BackgroundTasksChanged;

    /// <summary>
    /// Raised with the CLI's prediction of the user's next prompt, a few
    /// seconds after a turn ends. Not after every turn: the CLI skips it after
    /// the first answer, in plan mode, and when the user turned it off. It can
    /// arrive after the user has already sent the next message. Raised on a
    /// background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<string>? PromptSuggested;
}
