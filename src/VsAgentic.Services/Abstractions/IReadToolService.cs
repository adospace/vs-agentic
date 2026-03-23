namespace VsAgentic.Services.Abstractions;

public record ReadResult(string Content, int TotalLines, bool Truncated, string? Error);

public interface IReadToolService
{
    Task<ReadResult> ReadAsync(string filePath, int? offset, int? limit, CancellationToken cancellationToken = default);
}
