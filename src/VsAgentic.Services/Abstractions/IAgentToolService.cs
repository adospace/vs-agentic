namespace VsAgentic.Services.Abstractions;

public interface IAgentToolService
{
    Task<string> RunAsync(string task, string systemPrompt, string skill = "generic", CancellationToken cancellationToken = default);
}
