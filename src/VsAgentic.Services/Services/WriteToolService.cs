using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.Services;

public class WriteToolService(
    IOptions<VsAgenticOptions> options,
    IFileSessionTracker sessionTracker,
    IOutputListener outputListener,
    ILogger<WriteToolService> logger) : IWriteToolService
{
    private readonly VsAgenticOptions _options = options.Value;

    public async Task<WriteResult> WriteAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));
        if (content is null) throw new ArgumentNullException(nameof(content));

        var fullPath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(_options.WorkingDirectory, filePath);

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Write",
            Title = $"Write: {Path.GetFileName(fullPath)}",
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        try
        {
            var fileExists = File.Exists(fullPath);

            // Safety: must have read existing files before overwriting
            if (fileExists && !sessionTracker.HasBeenRead(fullPath))
            {
                item.Status = OutputItemStatus.Error;
                item.Body = "Must read existing file before overwriting";
                outputListener.OnStepCompleted(item);
                return new WriteResult(false, "Must read existing file before overwriting. Use the read tool first.");
            }

            // Detect encoding and line endings from existing file before overwriting
            System.Text.Encoding encoding = new System.Text.UTF8Encoding(false);
            string? existingLineEnding = null;
            if (fileExists)
            {
                var existingBytes = await Task.Run(() => File.ReadAllBytes(fullPath));
                encoding = TextFormatHelper.DetectEncoding(existingBytes);
                var existingText = encoding.GetString(existingBytes);
                existingLineEnding = TextFormatHelper.DetectLineEnding(existingText);

                sessionTracker.PushCheckpoint(fullPath, existingText);
            }

            // Normalize line endings to match the existing file (or OS default for new files)
            var targetLineEnding = existingLineEnding ?? Environment.NewLine;
            content = TextFormatHelper.NormalizeLineEndings(content, targetLineEnding);

            // Ensure parent directories exist
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Atomic write: write to temp file, then replace
            var tempPath = fullPath + ".tmp";
            try
            {
                await Task.Run(() => File.WriteAllText(tempPath, content, encoding));
                if (File.Exists(fullPath)) File.Delete(fullPath);
                File.Move(tempPath, fullPath);
            }
            catch
            {
                // Clean up temp file on failure
                try { File.Delete(tempPath); } catch { /* best effort */ }
                throw;
            }

            var created = !fileExists;

            logger.LogDebug("{Action} file '{FilePath}' ({Length} chars)",
                created ? "Created" : "Overwrote", fullPath, content.Length);

            // Mark as read so subsequent edits are allowed
            sessionTracker.MarkAsRead(fullPath);

            item.Status = OutputItemStatus.Success;
            item.Body = FormatBody(fullPath, created, content);
            outputListener.OnStepCompleted(item);

            return new WriteResult(created, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to write file '{FilePath}'", fullPath);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Error: {ex.Message}";
            outputListener.OnStepCompleted(item);

            return new WriteResult(false, ex.Message);
        }
    }

    private static string FormatBody(string filePath, bool created, string content)
    {
        var lineCount = content.Count(c => c == '\n') + (content.Length > 0 ? 1 : 0);
        var action = created ? "Created" : "Overwrote";
        return $"`{Path.GetFileName(filePath)}` — {action}, {lineCount} lines, {content.Length} chars";
    }
}
