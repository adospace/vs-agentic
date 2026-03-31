namespace VsAgentic.Services.Configuration;

/// <summary>
/// Determines how the extension communicates with Claude.
/// </summary>
public enum BackendMode
{
    /// <summary>
    /// Direct Anthropic API calls using the user's API key.
    /// Billed per token. Requires ANTHROPIC_API_KEY.
    /// </summary>
    ApiKey,

    /// <summary>
    /// Launches the Claude Code CLI as a subprocess.
    /// Uses the user's Claude subscription (Pro/Max). Requires 'claude' on PATH.
    /// </summary>
    ClaudeCli
}
