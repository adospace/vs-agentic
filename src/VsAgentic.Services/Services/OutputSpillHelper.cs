using System.Text;

namespace VsAgentic.Services.Services;

/// <summary>
/// When tool output exceeds the character budget, saves the full output to a temp file
/// and returns a short message with the file path so the AI can read it in chunks.
/// </summary>
internal static class OutputSpillHelper
{
    private const int DefaultMaxChars = 5000;
    private const string TempSubDir = "vs-agentic";

    /// <summary>
    /// If <paramref name="output"/> fits within <paramref name="maxChars"/>, returns it as-is.
    /// Otherwise writes the full output to a temp file and returns a short summary
    /// directing the AI to use the read tool.
    /// </summary>
    public static string SpillIfNeeded(string output, string label, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= maxChars)
            return output;

        var tempDir = Path.Combine(Path.GetTempPath(), TempSubDir);
        Directory.CreateDirectory(tempDir);

        var fileName = $"{label}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 6)}.txt";
        var tempFile = Path.Combine(tempDir, fileName);
        File.WriteAllText(tempFile, output);

        var lineCount = CountLines(output);
        var preview = GetPreview(output, maxChars / 4);

        var sb = new StringBuilder();
        sb.AppendLine(preview);
        sb.AppendLine();
        sb.AppendLine($"[output truncated — full output ({lineCount} lines, {output.Length} chars) saved to: {tempFile}]");
        sb.Append("Use the read tool with offset and limit to view the rest.");
        return sb.ToString();
    }

    /// <summary>
    /// Returns the first N complete lines that fit within the character budget.
    /// </summary>
    private static string GetPreview(string output, int maxPreviewChars)
    {
        var sb = new StringBuilder();
        var start = 0;

        while (start < output.Length && sb.Length < maxPreviewChars)
        {
            var newlineIdx = output.IndexOf('\n', start);
            var lineEnd = newlineIdx >= 0 ? newlineIdx : output.Length;
            var lineLength = lineEnd - start;

            // Would this line push us over budget?
            if (sb.Length + lineLength + 1 > maxPreviewChars)
                break;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(output, start, lineLength);
            start = lineEnd + 1;
        }

        return sb.ToString();
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        return count;
    }
}
