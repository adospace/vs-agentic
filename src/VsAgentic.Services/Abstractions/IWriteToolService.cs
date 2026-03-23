namespace VsAgentic.Services.Abstractions;

public record WriteResult(bool Created, string? Error);

public interface IWriteToolService
{
    Task<WriteResult> WriteAsync(string filePath, string content, CancellationToken cancellationToken = default);
}
