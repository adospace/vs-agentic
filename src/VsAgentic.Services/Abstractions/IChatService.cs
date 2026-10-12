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
    /// model. The turn's output arrives through the output listener as usual.
    /// Raised on a background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action? UnsolicitedTurnStarted;

    /// <summary>
    /// Raised when a turn announced by <see cref="UnsolicitedTurnStarted"/>
    /// ends, is stopped, or is cut off by the process exiting. Raised on a
    /// background thread.
    /// </summary>
    event Action? UnsolicitedTurnCompleted;

    /// <summary>
    /// Stops showing the turn the CLI started on its own. Like stopping a
    /// <see cref="SendMessageAsync"/> call, the CLI itself is not interrupted;
    /// the rest of the turn is dropped. A no-op when no such turn is running.
    /// </summary>
    void StopUnsolicitedTurn();

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
