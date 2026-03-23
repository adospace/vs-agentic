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
}
