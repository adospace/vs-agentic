namespace VsAgentic.Services.Abstractions;

public record WebFetchResult(string Content, int ContentLength, bool Truncated, string? Error);

public interface IWebFetchToolService
{
    Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}
