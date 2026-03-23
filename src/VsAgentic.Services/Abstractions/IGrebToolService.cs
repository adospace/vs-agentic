namespace VsAgentic.Services.Abstractions;

public record GrebMatch(string File, int LineNumber, string Line);

public record GrebResult(IReadOnlyList<GrebMatch> Matches, IReadOnlyList<string> MatchedFiles, string? Error);

public record GrebOptions
{
    public string? Glob { get; init; }
    public string? Path { get; init; }
    public bool CaseInsensitive { get; init; }
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public bool FilesOnly { get; init; }
}

public interface IGrebToolService
{
    Task<GrebResult> SearchAsync(string pattern, GrebOptions options, CancellationToken cancellationToken = default);
}
