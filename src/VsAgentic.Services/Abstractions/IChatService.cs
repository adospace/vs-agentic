namespace VsAgentic.Services.Abstractions;

public interface IChatService
{
    IAsyncEnumerable<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default);
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
    /// The model the CLI reported for the current session (e.g. "claude-opus-5"),
    /// taken from the <c>system/init</c> event. Null until the CLI process has
    /// started and emitted that event — i.e. before the first message is sent.
    /// </summary>
    string? CurrentModel { get; }

    /// <summary>
    /// The CLI's own session id, once one exists — either restored from persisted
    /// history or assigned when the session started. Lets the host locate the
    /// CLI's transcript for this session.
    /// </summary>
    string? CliSessionId { get; }

    /// <summary>
    /// Raised when <see cref="CurrentModel"/> changes — on session start and
    /// again after a process restart, which can pick up a different model.
    /// Raised on a background thread; hosts must marshal to the UI thread.
    /// </summary>
    event Action<string?>? ModelChanged;

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
}
