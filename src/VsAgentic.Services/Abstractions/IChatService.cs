using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.Abstractions;

public interface IChatService
{
    /// <summary>
    /// Gets or sets the model selection mode. Defaults to Auto.
    /// </summary>
    ModelMode ModelMode { get; set; }

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
    /// Returns the cumulative USD cost for this session based on token usage and model pricing.
    /// Returns null when no messages have been sent yet.
    /// </summary>
    decimal? GetSessionCost();

    /// <summary>
    /// Returns a snapshot of the current cumulative token usage for persistence.
    /// </summary>
    SessionTokenUsageSnapshot GetTokenUsageSnapshot();

    /// <summary>
    /// Restores previously persisted token usage into the session (called on session load).
    /// </summary>
    void RestoreTokenUsage(SessionTokenUsageSnapshot snapshot);
}
