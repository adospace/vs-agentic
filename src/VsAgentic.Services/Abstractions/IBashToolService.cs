namespace VsAgentic.Services.Abstractions;

public record BashResult(int ExitCode, string StandardOutput, string StandardError);

public interface IBashToolService
{
    Task<BashResult> ExecuteAsync(string command, string title, CancellationToken cancellationToken = default);
}
