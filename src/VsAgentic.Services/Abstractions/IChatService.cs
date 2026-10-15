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
    /// <param name="messageId">
    /// The id the message carries, so the caller can follow it through
    /// <see cref="MessageStarted"/> and cancel it with
    /// <see cref="CancelQueuedMessageAsync"/>. A new one is made when null.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the turn. The CLI is told to abort it too, and the call throws
    /// <see cref="OperationCanceledException"/> once the CLI has closed the
    /// turn, or after a short wait if it does not. Messages still queued are
    /// cancelled with it.
    /// </param>
    /// <remarks>
    /// A second call while a turn runs is allowed only when
    /// <see cref="CanQueueMessages"/> is true. The message then waits in the
    /// CLI, and the call returns when the turn that read it ends. A message
    /// cancelled before a turn read it returns with no output and no
    /// <see cref="MessageStarted"/>.
    /// </remarks>
    IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        IReadOnlyList<ChatImageAttachment>? images = null,
        string? messageId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a message can be sent while a turn runs. True when the running
    /// CLI reports message lifecycles: it then queues the message and reads it
    /// into the running turn at the next step, or starts a turn for it after
    /// the running one. False before the first turn of a process has reported
    /// its capabilities, and on a CLI that predates them.
    /// </summary>
    bool CanQueueMessages { get; }

    /// <summary>
    /// Takes a queued message out of the CLI's queue. Returns false when a
    /// turn has already read it, or the CLI did not answer; the message then
    /// runs as sent.
    /// </summary>
    Task<bool> CancelQueuedMessageAsync(string messageId);

    /// <summary>
    /// Raised with a message's id when a turn reads it: at once for a message
    /// sent while idle, and at the next step of the running turn for a queued
    /// one. Raised on a background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<string>? MessageStarted;
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
}
