using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.Services;

public class ReadToolService(
    IOptions<VsAgenticOptions> options,
    IFileSessionTracker sessionTracker,
    IOutputListener outputListener,
    ILogger<ReadToolService> logger) : IReadToolService
{
    private readonly VsAgenticOptions _options = options.Value;

    public async Task<ReadResult> ReadAsync(string filePath, int? offset, int? limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));

        var fullPath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(_options.WorkingDirectory, filePath);

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Read",
            Title = $"Read: {Path.GetFileName(fullPath)}",
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        try
        {
            if (!File.Exists(fullPath))
            {
                item.Status = OutputItemStatus.Error;
                item.Body = $"File not found: {fullPath}";
                outputListener.OnStepCompleted(item);
                return new ReadResult("", 0, false, $"File not found: {fullPath}");
            }

            logger.LogDebug("Reading file '{FilePath}' (offset={Offset}, limit={Limit})", fullPath, offset, limit);

            var allLines = await Task.Run(() => File.ReadAllLines(fullPath));
            var totalLines = allLines.Length;

            var startLine = Math.Min(Math.Max(offset ?? 0, 0), totalLines);
            var lineCount = limit ?? (totalLines - startLine);
            lineCount = Math.Min(Math.Max(lineCount, 0), totalLines - startLine);

            // Format with line numbers (1-based)
            var numbered = new string[lineCount];
            for (var i = 0; i < lineCount; i++)
            {
                numbered[i] = $"{startLine + i + 1}\t{allLines[startLine + i]}";
            }

            var content = string.Join("\n", numbered);

            // Truncate if content exceeds MaxOutputChars
            var truncated = false;
            if (content.Length > _options.MaxOutputChars)
            {
                content = content.Substring(0, _options.MaxOutputChars) + $"\n... [truncated at {_options.MaxOutputChars} chars]";
                truncated = true;
            }

            // Track that this file has been read (enables editing)
            sessionTracker.MarkAsRead(fullPath);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(fullPath, totalLines, startLine, lineCount, truncated);
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Read {LineCount} lines from '{FilePath}' (total {TotalLines})", lineCount, fullPath, totalLines);

            return new ReadResult(content, totalLines, truncated, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to read file '{FilePath}'", fullPath);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Error: {ex.Message}";
            outputListener.OnStepCompleted(item);

            return new ReadResult("", 0, false, ex.Message);
        }
    }

    private static string FormatBody(string filePath, int totalLines, int startLine, int lineCount, bool truncated)
    {
        var range = $"lines {startLine + 1}-{startLine + lineCount} of {totalLines}";
        var summary = $"`{Path.GetFileName(filePath)}` — {range}";
        if (truncated)
            summary += " (truncated)";
        return summary;
    }
}
