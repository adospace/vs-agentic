namespace VsAgentic.Services.Abstractions;

public record GropResult(IReadOnlyList<string> Matches, string? Error);

public interface IGropToolService
{
    Task<GropResult> FindAsync(string pattern, string? path, CancellationToken cancellationToken = default);
}
