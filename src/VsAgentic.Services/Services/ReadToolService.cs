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

        logger.LogTrace("[Read] Args received — filePath: {FilePath}, offset: {Offset}, limit: {Limit}", filePath, offset, limit);

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

            var allLines = await Task.Run(() => TextFormatHelper.ReadFileLinesShared(fullPath));
            var totalLines = allLines.Length;

            var startLine = Math.Min(Math.Max(offset ?? 0, 0), totalLines);
            // Apply default line limit when caller doesn't specify one
            var maxLines = _options.MaxReadLines;
            var lineCount = limit ?? Math.Min(totalLines - startLine, maxLines);
            lineCount = Math.Min(Math.Max(lineCount, 0), totalLines - startLine);

            var truncated = (startLine + lineCount) < totalLines;

            // Format with right-aligned line numbers and a clear separator.
            // Using " | " instead of "\t" avoids ambiguity with tab-indented content.
            var maxLineNumber = startLine + lineCount;
            var lineNumWidth = maxLineNumber.ToString().Length;
            var numbered = new string[lineCount];
            for (var i = 0; i < lineCount; i++)
            {
                var lineNum = (startLine + i + 1).ToString().PadLeft(lineNumWidth);
                numbered[i] = $"{lineNum} | {allLines[startLine + i]}";
            }

            var content = string.Join("\n", numbered);

            // Safety net: truncate at char limit if individual lines are extremely long
            if (content.Length > _options.MaxOutputChars)
            {
                // Re-truncate at a line boundary instead of mid-line
                var charBudget = _options.MaxOutputChars;
                var linesInBudget = 0;
                var charCount = 0;
                for (var i = 0; i < numbered.Length; i++)
                {
                    var lineLen = numbered[i].Length + (i > 0 ? 1 : 0); // +1 for \n separator
                    if (charCount + lineLen > charBudget) break;
                    charCount += lineLen;
                    linesInBudget++;
                }

                if (linesInBudget < numbered.Length && linesInBudget > 0)
                {
                    content = string.Join("\n", numbered.Take(linesInBudget));
                    truncated = true;
                }
            }

            // Track that this file has been read (enables editing)
            sessionTracker.MarkAsRead(fullPath);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(fullPath, totalLines, startLine, lineCount, truncated);
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Read {LineCount} lines from '{FilePath}' (total {TotalLines})", lineCount, fullPath, totalLines);
            logger.LogTrace("[Read] Result — {ContentLength} chars, {LineCount} lines, truncated: {Truncated}", content.Length, lineCount, truncated);
            logger.LogTrace("[Read] Content returned to AI:\n{Content}", content);

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
