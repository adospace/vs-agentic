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
}
