using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.Services;

public class GropToolService(
    IOptions<VsAgenticOptions> options,
    IOutputListener outputListener,
    ILogger<GropToolService> logger) : IGropToolService
{
    private readonly VsAgenticOptions _options = options.Value;
    private const int MaxResults = 200;

    public Task<GropResult> FindAsync(string pattern, string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(pattern));

        logger.LogTrace("[Grop] Args received — pattern: {Pattern}, path: {Path}", pattern, path);

        var searchDir = path ?? _options.WorkingDirectory;

        if (!Directory.Exists(searchDir))
            return Task.FromResult(new GropResult([], $"Directory does not exist: {searchDir}"));

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Grop",
            Title = $"Find: {pattern}",
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        try
        {
            logger.LogDebug("Grop searching for pattern '{Pattern}' in '{Directory}'", pattern, searchDir);

            // If the pattern doesn't contain a path separator or recursive wildcard,
            // auto-prepend **/ so it searches recursively (e.g. "*.cs" → "**/*.cs")
            var effectivePattern = pattern;
            if (!pattern.Contains('/') && !pattern.Contains('\\') && !pattern.Contains("**"))
                effectivePattern = "**/" + pattern;

            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(effectivePattern);

            var result = matcher.Execute(
                new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                    new DirectoryInfo(searchDir)));

            var matches = result.Files
                .Select(f => f.Path)
                .OrderBy(x => x)
                .Take(MaxResults + 1)
                .ToList();

            var truncated = matches.Count > MaxResults;
            if (truncated)
                matches = matches.Take(MaxResults).ToList();

            var gropResult = new GropResult(matches, truncated ? $"Results truncated to {MaxResults} entries." : null);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(pattern, matches, truncated);
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Grop found {Count} matches", matches.Count);
            logger.LogTrace("[Grop] Result — {Count} matches, truncated: {Truncated}", matches.Count, truncated);

            return Task.FromResult(gropResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Grop failed for pattern '{Pattern}'", pattern);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Error: {ex.Message}";
            outputListener.OnStepCompleted(item);

            return Task.FromResult(new GropResult([], ex.Message));
        }
    }

    private static string FormatBody(string pattern, List<string> matches, bool truncated)
    {
        var summary = $"Pattern `{pattern}` — {matches.Count} match{(matches.Count == 1 ? "" : "es")}";
        if (truncated)
            summary += $" (truncated to {MaxResults})";

        if (matches.Count == 0)
            return summary;

        return summary + "\n" + string.Join("\n", matches);
    }
}
