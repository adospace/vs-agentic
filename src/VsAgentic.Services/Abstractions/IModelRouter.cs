using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.Abstractions;

public interface IModelRouter
{
    ModelMode Mode { get; set; }

    /// <summary>
    /// Resolves the model ID to use for the given user message.
    /// In Auto mode, uses Haiku to classify complexity. Otherwise returns the fixed model for the current mode.
    /// </summary>
    Task<string> ResolveModelAsync(string userMessage, int conversationDepth, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a short title for a conversation based on the first user message.
    /// Uses Haiku for fast, cheap title generation.
    /// </summary>
    Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default);
}
