namespace VsAgentic.Services.Abstractions;

/// <summary>
/// The task-level hint tells the agent which model tier to use.
/// </summary>
public enum AgentTaskLevel
{
    /// <summary>Lightweight — runs on Haiku.</summary>
    Light,

    /// <summary>Standard — runs on Sonnet.</summary>
    Standard,

    /// <summary>Heavy reasoning / planning — runs on Opus.</summary>
    Heavy
}

public interface IAgentToolService
{
    Task<string> RunAsync(string task, string systemPrompt, string skill = "generic",
        AgentTaskLevel level = AgentTaskLevel.Light, CancellationToken cancellationToken = default);
}
