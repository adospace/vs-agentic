using System.Text.RegularExpressions;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.Services;

public class GrebToolService(
    IOptions<VsAgenticOptions> options,
    IOutputListener outputListener,
    ILogger<GrebToolService> logger) : IGrebToolService
{
    private readonly VsAgenticOptions _options = options.Value;
    private const int MaxMatches = 500;
    private const int MaxFileSize = 1024 * 1024; // 1 MB

    public async Task<GrebResult> SearchAsync(string pattern, GrebOptions grebOptions, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(pattern));

        logger.LogTrace("[Greb] Args received — pattern: {Pattern}, glob: {Glob}, path: {Path}, caseInsensitive: {CI}, filesOnly: {FO}, contextBefore: {CB}, contextAfter: {CA}",
            pattern, grebOptions.Glob, grebOptions.Path, grebOptions.CaseInsensitive, grebOptions.FilesOnly, grebOptions.ContextBefore, grebOptions.ContextAfter);

        var searchDir = grebOptions.Path ?? _options.WorkingDirectory;

        if (!Directory.Exists(searchDir))
            return new GrebResult([], [], $"Directory does not exist: {searchDir}");

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Greb",
            Title = $"Search: {pattern}",
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        try
        {
            logger.LogDebug("Greb searching for pattern '{Pattern}' in '{Directory}'", pattern, searchDir);

            var regexOptions = RegexOptions.Compiled;
            if (grebOptions.CaseInsensitive)
                regexOptions |= RegexOptions.IgnoreCase;

            var regex = new Regex(pattern, regexOptions, TimeSpan.FromSeconds(5));

            var files = GetFilesToSearch(searchDir, grebOptions.Glob);

            var allMatches = new List<GrebMatch>();
            var matchedFiles = new List<string>();
            var truncated = false;

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = Path.Combine(searchDir, filePath);

                if (!File.Exists(fullPath))
                    continue;

                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > MaxFileSize)
                    continue;

                // Skip binary files by checking for null bytes in the first chunk
                if (IsBinaryFile(fullPath))
                    continue;

                var lines = await Task.Run(() => TextFormatHelper.ReadFileLinesShared(fullPath));
                var fileHasMatch = false;

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!regex.IsMatch(lines[i]))
                        continue;

                    fileHasMatch = true;

                    if (grebOptions.FilesOnly)
                        break;

                    // Add context-before lines
                    var contextStart = Math.Max(0, i - grebOptions.ContextBefore);
                    for (var cb = contextStart; cb < i; cb++)
                    {
                        allMatches.Add(new GrebMatch(filePath, cb + 1, lines[cb]));
                    }

                    // Add the matching line
                    allMatches.Add(new GrebMatch(filePath, i + 1, lines[i]));

                    // Add context-after lines
                    var contextEnd = Math.Min(lines.Length - 1, i + grebOptions.ContextAfter);
                    for (var ca = i + 1; ca <= contextEnd; ca++)
                    {
                        allMatches.Add(new GrebMatch(filePath, ca + 1, lines[ca]));
                    }

                    if (allMatches.Count >= MaxMatches)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (fileHasMatch)
                    matchedFiles.Add(filePath);

                if (truncated)
                    break;
            }

            var error = truncated ? $"Results truncated to {MaxMatches} matches." : null;
            var result = new GrebResult(allMatches, matchedFiles, error);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(pattern, grebOptions, allMatches, matchedFiles, truncated);
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Greb found {MatchCount} matches in {FileCount} files", allMatches.Count, matchedFiles.Count);
            logger.LogTrace("[Greb] Result — {MatchCount} matches in {FileCount} files, truncated: {Truncated}",
                allMatches.Count, matchedFiles.Count, truncated);

            return result;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid regex pattern: '{Pattern}'", pattern);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Invalid regex: {ex.Message}";
            outputListener.OnStepCompleted(item);

            return new GrebResult([], [], $"Invalid regex pattern: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Greb failed for pattern '{Pattern}'", pattern);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Error: {ex.Message}";
            outputListener.OnStepCompleted(item);

            return new GrebResult([], [], ex.Message);
        }
    }

    private static IEnumerable<string> GetFilesToSearch(string searchDir, string? glob)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob ?? "**/*");

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(searchDir)));
        return result.Files.Select(f => f.Path);
    }

    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            var buffer = new byte[512];
            using var stream = File.OpenRead(filePath);
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, bytesRead) >= 0;
        }
        catch
        {
            return true;
        }
    }

    private static string FormatBody(string pattern, GrebOptions grebOptions, List<GrebMatch> matches, List<string> matchedFiles, bool truncated)
    {
        var summary = grebOptions.FilesOnly
            ? $"Pattern `{pattern}` — {matchedFiles.Count} file{(matchedFiles.Count == 1 ? "" : "s")}"
            : $"Pattern `{pattern}` — {matches.Count} match{(matches.Count == 1 ? "" : "es")} in {matchedFiles.Count} file{(matchedFiles.Count == 1 ? "" : "s")}";

        if (truncated)
            summary += " (truncated)";

        if (grebOptions.FilesOnly)
        {
            return matchedFiles.Count == 0
                ? summary
                : summary + "\n" + string.Join("\n", matchedFiles);
        }

        if (matches.Count == 0)
            return summary;

        var lines = matches.Select(m => $"{m.File}:{m.LineNumber}: {m.Line}");
        return summary + "\n" + string.Join("\n", lines);
    }
}
