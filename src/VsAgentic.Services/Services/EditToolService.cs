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
                    // Fallback: whitespace-tolerant matching.
                    // LLMs consistently miscount leading spaces in tool call arguments.
                    // Match by trimmed line content to LOCATE the region, then apply
                    // the replacement preserving the FILE's actual indentation.
                    var flexResult = TryWhitespaceTolerantMatch(contents, oldString, newString, replaceAll);
                    if (flexResult != null)
                    {
                        logger.LogTrace("[Edit] Whitespace-tolerant match succeeded");

                        sessionTracker.PushCheckpoint(fullPath, contents);

                        var flexOutput = TextFormatHelper.NormalizeLineEndings(flexResult.Value.Result, originalLineEnding);
                        await Task.Run(() => TextFormatHelper.WriteFileShared(fullPath, flexOutput, encoding));

                        var flexAffected = GetAffectedLines(flexOutput,
                            TextFormatHelper.NormalizeLineEndings(flexResult.Value.NewContent, originalLineEnding));

                        item.Status = OutputItemStatus.Success;
                        item.Body = FormatBody(fullPath, flexResult.Value.Replacements, flexAffected);
                        outputListener.OnStepCompleted(item);

                        logger.LogDebug("Edited '{FilePath}': {Replacements} replacement(s) (whitespace-tolerant)", fullPath, flexResult.Value.Replacements);

                        return new EditResult(flexResult.Value.Replacements, flexAffected, null);
                    }

                    // Truly not found — show hint
                    var hint = FindNearestMatchHint(contents, oldString);
                    var errorMsg = $"old_string not found in {Path.GetFileName(fullPath)}. The text must match the file content exactly, including whitespace and indentation. Re-read the file to see the current content.";
                    if (hint != null)
                        errorMsg += $"\n\nNearest partial match around line {hint.Value.Line}:\n{hint.Value.Snippet}";

                    logger.LogTrace("[Edit] No match found. Hint: {Hint}", hint?.Snippet ?? "(none)");

                    item.Status = OutputItemStatus.Error;
                    item.Body = "old_string not found";
                    outputListener.OnStepCompleted(item);
                    return new EditResult(0, [], errorMsg);
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
        return $"`{Path.GetFileName(filePath)}` — {replacements} replacement(s)";
    }

    /// <summary>
    /// Whitespace-tolerant matching: matches by trimmed line content, then applies
    /// the replacement using the FILE's actual indentation (not the AI's broken indentation).
    ///
    /// The AI's newString indentation relative to its oldString expresses INTENT
    /// (e.g. "indent this 2 more spaces"). We compute that relative intent and apply
    /// it on top of the file's real indentation.
    /// </summary>
    private static (string Result, int Replacements, string NewContent)? TryWhitespaceTolerantMatch(
        string contents, string oldString, string newString, bool replaceAll)
    {
        var contentLines = contents.Split('\n');
        var oldLines = oldString.Split('\n');
        var newLines = newString.Split('\n');

        if (oldLines.Length == 0) return null;

        // Trim each old line for comparison
        var oldTrimmed = oldLines.Select(l => l.TrimStart()).ToArray();

        // Skip empty trailing lines in oldTrimmed for matching purposes
        var matchLength = oldTrimmed.Length;
        while (matchLength > 0 && string.IsNullOrWhiteSpace(oldTrimmed[matchLength - 1]))
            matchLength--;
        if (matchLength == 0) return null;

        // Find matches by scanning content lines
        var matchPositions = new List<int>();
        for (var i = 0; i <= contentLines.Length - matchLength; i++)
        {
            var match = true;
            for (var j = 0; j < matchLength; j++)
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

        // Build the replacement using the FILE's indentation as the base.
        // For each new line, compute: fileBaseIndent + (aiNewIndent - aiOldBaseIndent)
        // where aiOldBaseIndent is the indentation of the first non-empty AI oldString line.
        var aiOldBaseIndent = GetLeadingWhitespace(oldLines.FirstOrDefault(l => l.TrimStart().Length > 0) ?? "");
        var aiOldBaseLen = ExpandToSpaces(aiOldBaseIndent);

        var resultLines = contentLines.ToList();
        var replacements = replaceAll ? matchPositions.Count : 1;
        string? reindentedNewJoined = null;

        for (var m = matchPositions.Count - 1; m >= 0; m--)
        {
            if (!replaceAll && m > 0) continue;

            var matchStart = matchPositions[m];

            // The file's actual indentation at the match start
            var fileBaseIndent = GetLeadingWhitespace(contentLines[matchStart]);
            var fileBaseLen = ExpandToSpaces(fileBaseIndent);
            // Use the same indent char as the file (spaces or tabs)
            var useTabs = fileBaseIndent.Contains('\t');

            var reindentedNew = new List<string>();
            for (var j = 0; j < newLines.Length; j++)
            {
                var trimmed = newLines[j].TrimStart();
                if (trimmed.Length == 0)
                {
                    reindentedNew.Add("");
                    continue;
                }

                // How much does the AI want this line indented relative to its base?
                var aiNewIndentLen = ExpandToSpaces(GetLeadingWhitespace(newLines[j]));
                var relativeIndent = aiNewIndentLen - aiOldBaseLen;

                // Apply that relative indent to the file's base
                var targetIndentLen = Math.Max(0, fileBaseLen + relativeIndent);
                var targetIndent = useTabs
                    ? new string('\t', targetIndentLen / 4) + new string(' ', targetIndentLen % 4)
                    : new string(' ', targetIndentLen);

                reindentedNew.Add(targetIndent + trimmed);
            }

            reindentedNewJoined = string.Join("\n", reindentedNew);

            // Replace the matched lines (use full oldLines.Length to include trailing empties)
            resultLines.RemoveRange(matchStart, oldLines.Length);
            resultLines.InsertRange(matchStart, reindentedNew);
        }

        return (string.Join("\n", resultLines), replacements, reindentedNewJoined ?? newString);
    }

    private static string GetLeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;
        return line.Substring(0, i);
    }

    /// <summary>
    /// Expands tabs to spaces (4-space tab stops) for indent comparison.
    /// </summary>
    private static int ExpandToSpaces(string indent)
    {
        var count = 0;
        foreach (var c in indent)
        {
            if (c == '\t') count += 4;
            else count++;
        }
        return count;
    }

    /// <summary>
    /// Finds the closest partial match for oldString in the file contents to help the AI
    /// understand what went wrong. Matches by the first non-empty trimmed line of oldString.
    /// </summary>
    private static (int Line, string Snippet)? FindNearestMatchHint(string contents, string oldString)
    {
        var oldLines = oldString.Split('\n');
        // Find the first non-empty line to use as search anchor
        var anchorLine = oldLines.FirstOrDefault(l => l.Trim().Length > 0);
        if (anchorLine == null) return null;
        var anchor = anchorLine.Trim();

        var contentLines = contents.Split('\n');
        for (var i = 0; i < contentLines.Length; i++)
        {
            if (contentLines[i].Trim().Contains(anchor) ||
                (anchor.Length > 20 && contentLines[i].Trim().Contains(anchor.Substring(0, 20))))
            {
                // Show a few lines of context around the match
                var start = Math.Max(0, i - 1);
                var end = Math.Min(contentLines.Length, i + 4);
                var snippetLines = new List<string>();
                for (var j = start; j < end; j++)
                {
                    snippetLines.Add($"{j + 1}\t{contentLines[j]}");
                }
                return (i + 1, string.Join("\n", snippetLines));
            }
        }

        return null;
    }
}
