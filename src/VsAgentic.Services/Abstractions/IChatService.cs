using VsAgentic.Services.Configuration;

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
    /// Both are start-up flags on the CLI, so the running process is torn down
    /// here and the next <see cref="SendMessageAsync"/> starts a fresh one that
    /// resumes the same conversation. Nothing is lost, but the next message
    /// pays the process-start cost. A no-op when neither value changed.
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
}
