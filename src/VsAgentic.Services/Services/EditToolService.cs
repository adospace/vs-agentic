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
            // Safety: must have read the file first
            if (!sessionTracker.HasBeenRead(fullPath))
            {
                item.Status = OutputItemStatus.Error;
                item.Body = "Must read file before editing";
                outputListener.OnStepCompleted(item);
                return new EditResult(0, [], "Must read file before editing. Use the read tool first.");
            }

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

            var contents = await Task.Run(() => File.ReadAllText(fullPath));

            // Count matches
            var count = CountOccurrences(contents, oldString);

            if (count == 0)
            {
                item.Status = OutputItemStatus.Error;
                item.Body = "old_string not found";
                outputListener.OnStepCompleted(item);
                return new EditResult(0, [], $"old_string not found in {Path.GetFileName(fullPath)}. Make sure it matches exactly, including whitespace and indentation.");
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

            await Task.Run(() => File.WriteAllText(fullPath, result));

            // Calculate affected line numbers
            var affectedLines = GetAffectedLines(result, newString);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(fullPath, replacements, affectedLines);
            outputListener.OnStepCompleted(item);

            logger.LogDebug("Edited '{FilePath}': {Replacements} replacement(s)", fullPath, replacements);

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
}
