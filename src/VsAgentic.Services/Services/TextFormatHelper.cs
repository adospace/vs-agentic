namespace VsAgentic.Services.Services;

/// <summary>
/// Shared helpers for detecting and normalizing line endings and encoding
/// across the file-manipulation tools (Write, Edit).
/// </summary>
internal static class TextFormatHelper
{
    /// <summary>
    /// Detects the dominant line ending style in the given text.
    /// Returns "\r\n", "\r", or "\n" (or null if no line endings found).
    /// </summary>
    public static string? DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++; // skip the \n
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
            return null;

        // Return the most common line ending
        if (crlf >= lf && crlf >= cr) return "\r\n";
        if (lf >= cr) return "\n";
        return "\r";
    }

    /// <summary>
    /// Normalizes all line endings in the content to the specified line ending.
    /// </summary>
    public static string NormalizeLineEndings(string content, string lineEnding)
    {
        // First normalize everything to \n, then replace with target
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (lineEnding != "\n")
            normalized = normalized.Replace("\n", lineEnding);
        return normalized;
    }

    /// <summary>
    /// Detects the encoding of a file from its byte order mark, falling back to UTF-8 without BOM.
    /// </summary>
    public static System.Text.Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new System.Text.UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode; // UTF-16 LE
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode; // UTF-16 BE
        return new System.Text.UTF8Encoding(false);
    }

    /// <summary>
    /// Reads a file using FileShare.ReadWrite so it works even when VS or another process holds the file open.
    /// </summary>
    public static string ReadFileShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Reads a file as lines using FileShare.ReadWrite.
    /// </summary>
    public static string[] ReadFileLinesShared(string path)
    {
        var content = ReadFileShared(path);
        return content.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.None);
    }

    /// <summary>
    /// Reads a file's raw bytes using FileShare.ReadWrite.
    /// </summary>
    public static byte[] ReadFileBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new System.IO.MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Reads a file detecting encoding, returns content + encoding. Uses FileShare.ReadWrite.
    /// </summary>
    public static (string Content, System.Text.Encoding Encoding) ReadFileWithEncoding(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        var content = sr.ReadToEnd();
        return (content, sr.CurrentEncoding);
    }

    /// <summary>
    /// Writes a file using FileShare.ReadWrite so it works even when VS holds the file open.
    /// </summary>
    public static void WriteFileShared(string path, string content, System.Text.Encoding encoding)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var sw = new StreamWriter(fs, encoding);
        sw.Write(content);
    }
}
