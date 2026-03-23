using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.Services;

public class EditToolService(
    IOptions<VsAgenticOptions> options,
    IFileSessionTracker sessionTracker,
    IOutputListener outputListener,
    ILogger<EditToolService> logger) : IEditToolService
{
    private readonly VsAgenticOptions _options = options.Value;

    public async Task<EditResult> EditAsync(string filePath, string oldString, string newString, bool replaceAll = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));
        if (string.IsNullOrWhiteSpace(oldString)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(oldString));
        if (newString is null) throw new ArgumentNullException(nameof(newString));

        logger.LogTrace("[Edit] Args received — filePath: {FilePath}, replaceAll: {ReplaceAll}", filePath, replaceAll);
        logger.LogTrace("[Edit] oldString ({OldLen} chars):\n{OldString}", oldString.Length, oldString);
        logger.LogTrace("[Edit] newString ({NewLen} chars):\n{NewString}", newString.Length, newString);

        var fullPath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(_options.WorkingDirectory, filePath);

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Edit",
            Title = $"Edit: {Path.GetFileName(fullPath)}",
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        try
        {
            // Safety: no-op detection
            if (oldString == newString)
            {
                item.Status = OutputItemStatus.Error;
                item.Body = "old_string and new_string are identical";
                outputListener.OnStepCompleted(item);
                return new EditResult(0, [], "old_string and new_string are identical.");
            }

            if (!File.Exists(fullPath))
            {
                item.Status = OutputItemStatus.Error;
                item.Body = $"File not found: {fullPath}";
                outputListener.OnStepCompleted(item);
                return new EditResult(0, [], $"File not found: {fullPath}");
            }

            logger.LogDebug("Editing file '{FilePath}' (replaceAll={ReplaceAll})", fullPath, replaceAll);

            // Detect encoding so we preserve BOM / charset on write-back
            var (contents, encoding) = await Task.Run(() => TextFormatHelper.ReadFileWithEncoding(fullPath));

            // Handle line ending mismatches between AI input (\n) and file content (\r\n or mixed).
            // Strategy: try exact match first. If that fails, normalize everything to \n,
            // do the replacement, then re-normalize the result to the file's dominant line ending.
            var count = CountOccurrences(contents, oldString);
            // Detect the file's dominant line ending before any normalization
            var originalLineEnding = TextFormatHelper.DetectLineEnding(contents) ?? Environment.NewLine;

            if (count == 0)
            {
                // Try line-ending-insensitive matching: normalize everything to \n
                contents = TextFormatHelper.NormalizeLineEndings(contents, "\n");
                oldString = TextFormatHelper.NormalizeLineEndings(oldString, "\n");
                newString = TextFormatHelper.NormalizeLineEndings(newString, "\n");
                count = CountOccurrences(contents, oldString);

                if (count == 0)
                {
                    // Fallback: whitespace-flexible matching.
                    // The AI often gets leading indentation wrong when reconstructing from read output.
                    // Try matching by trimming leading whitespace on each line, then apply the
                    // replacement using the original file's indentation.
                    var flexResult = TryFlexibleWhitespaceMatch(contents, oldString, newString, replaceAll);
                    if (flexResult != null)
                    {
                        logger.LogTrace("[Edit] Whitespace-flexible match succeeded");

                        // Checkpoint before mutation
                        sessionTracker.PushCheckpoint(fullPath, contents);

                        var flexOutput = TextFormatHelper.NormalizeLineEndings(flexResult.Value.Result, originalLineEnding);
                        await Task.Run(() => TextFormatHelper.WriteFileShared(fullPath, flexOutput, encoding));

                        var flexAffected = GetAffectedLines(flexOutput,
                            TextFormatHelper.NormalizeLineEndings(newString, originalLineEnding));

                        item.Status = OutputItemStatus.Success;
                        item.Body = FormatBody(fullPath, flexResult.Value.Replacements, flexAffected);
                        outputListener.OnStepCompleted(item);

                        logger.LogDebug("Edited '{FilePath}': {Replacements} replacement(s) (whitespace-flexible match)", fullPath, flexResult.Value.Replacements);
                        logger.LogTrace("[Edit] Result — {Replacements} replacement(s), affected lines: {AffectedLines}",
                            flexResult.Value.Replacements, string.Join(", ", flexAffected));

                        return new EditResult(flexResult.Value.Replacements, flexAffected, null);
                    }

                    item.Status = OutputItemStatus.Error;
                    item.Body = "old_string not found";
                    outputListener.OnStepCompleted(item);
                    return new EditResult(0, [], $"old_string not found in {Path.GetFileName(fullPath)}. Make sure it matches exactly, including whitespace and indentation.");
                }
            }

            if (count > 1 && !replaceAll)
            {
                item.Status = OutputItemStatus.Error;
                item.Body = $"old_string found {count} times — ambiguous";
                outputListener.OnStepCompleted(item);
                return new EditResult(0, [], $"old_string found {count} times in {Path.GetFileName(fullPath)}. Provide more surrounding context to make it unique, or set replace_all=true.");
            }

            // Checkpoint before mutation
            sessionTracker.PushCheckpoint(fullPath, contents);

            // Apply replacement
            var replacements = replaceAll ? count : 1;
            var result = replaceAll
                ? contents.Replace(oldString, newString)
                : ReplaceFirst(contents, oldString, newString);

            // Ensure the result uses the file's original dominant line ending
            result = TextFormatHelper.NormalizeLineEndings(result, originalLineEnding);

            await Task.Run(() => TextFormatHelper.WriteFileShared(fullPath, result, encoding));

            // Calculate affected line numbers (newString must match the result's line endings)
            var normalizedNewString = TextFormatHelper.NormalizeLineEndings(newString, originalLineEnding);
            var affectedLines = GetAffectedLines(result, normalizedNewString);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(fullPath, replacements, affectedLines);
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Edited '{FilePath}': {Replacements} replacement(s)", fullPath, replacements);
            logger.LogTrace("[Edit] Result — {Replacements} replacement(s), affected lines: {AffectedLines}",
                replacements, string.Join(", ", affectedLines));

            return new EditResult(replacements, affectedLines, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to edit file '{FilePath}'", fullPath);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Error: {ex.Message}";
            outputListener.OnStepCompleted(item);

            return new EditResult(0, [], ex.Message);
        }
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) return text;
        return text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
    }

    private static List<int> GetAffectedLines(string contents, string newString)
    {
        var lines = new List<int>();
        var index = 0;
        while ((index = contents.IndexOf(newString, index, StringComparison.Ordinal)) >= 0)
        {
            var lineNumber = contents.Substring(0, index).Count(c => c == '\n') + 1;
            var newStringLineCount = newString.Count(c => c == '\n');
            for (var i = 0; i <= newStringLineCount; i++)
                lines.Add(lineNumber + i);
            index += newString.Length;
        }
        return lines;
    }

    private static string FormatBody(string filePath, int replacements, IReadOnlyList<int> affectedLines)
    {
        var linesInfo = affectedLines.Count > 0
            ? $"lines {string.Join(", ", affectedLines)}"
            : "applied";
        return $"`{Path.GetFileName(filePath)}` — {replacements} replacement(s), {linesInfo}";
    }

    /// <summary>
    /// Tries to match oldString against contents by comparing lines with leading whitespace trimmed.
    /// If a unique match is found, applies the replacement preserving the original file's indentation.
    /// </summary>
    private static (string Result, int Replacements)? TryFlexibleWhitespaceMatch(
        string contents, string oldString, string newString, bool replaceAll)
    {
        var contentLines = contents.Split('\n');
        var oldLines = oldString.Split('\n');

        if (oldLines.Length == 0) return null;

        // Trim each old line for comparison
        var oldTrimmed = oldLines.Select(l => l.TrimStart()).ToArray();

        // Find matches by scanning content lines
        var matchPositions = new List<int>(); // line indices where matches start
        for (var i = 0; i <= contentLines.Length - oldLines.Length; i++)
        {
            var match = true;
            for (var j = 0; j < oldLines.Length; j++)
            {
                if (contentLines[i + j].TrimStart() != oldTrimmed[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                matchPositions.Add(i);
        }

        if (matchPositions.Count == 0) return null;
        if (matchPositions.Count > 1 && !replaceAll) return null;

        var newLines = newString.Split('\n');

        // Process matches in reverse order so line indices remain valid
        var resultLines = contentLines.ToList();
        var replacements = replaceAll ? matchPositions.Count : 1;

        for (var m = matchPositions.Count - 1; m >= 0; m--)
        {
            if (!replaceAll && m > 0) continue;

            var matchStart = matchPositions[m];

            // Detect the indentation of the first matched line in the original file
            var originalIndent = GetLeadingWhitespace(contentLines[matchStart]);
            var oldIndent = GetLeadingWhitespace(oldLines[0]);

            // Re-indent newString lines to match the file's indentation
            var reindentedNew = new List<string>();
            for (var j = 0; j < newLines.Length; j++)
            {
                var newLine = newLines[j];
                var newLineContent = newLine.TrimStart();
                if (newLineContent.Length == 0)
                {
                    reindentedNew.Add("");
                    continue;
                }

                if (j == 0)
                {
                    // First line: use the original file's indentation
                    reindentedNew.Add(originalIndent + newLineContent);
                }
                else
                {
                    // Subsequent lines: calculate relative indent from the AI's first line,
                    // then apply the same relative offset from the file's indentation
                    var aiLineIndent = GetLeadingWhitespace(newLines[j]);
                    var relativeIndent = aiLineIndent.Length > oldIndent.Length
                        ? aiLineIndent.Substring(oldIndent.Length)
                        : "";
                    reindentedNew.Add(originalIndent + relativeIndent + newLineContent);
                }
            }

            // Replace the matched lines
            resultLines.RemoveRange(matchStart, oldLines.Length);
            resultLines.InsertRange(matchStart, reindentedNew);
        }

        return (string.Join("\n", resultLines), replacements);
    }

    private static string GetLeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;
        return line.Substring(0, i);
    }
}
