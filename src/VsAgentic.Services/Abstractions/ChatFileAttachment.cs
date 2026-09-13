using System.IO;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// A non-image file (or folder) travelling with a user message. Only the path
/// goes out: the CLI reads the file itself, so a 40 MB log costs nothing until
/// the model actually opens it, and formats the API would reject — archives,
/// binaries, spreadsheets — still work.
/// </summary>
public sealed class ChatFileAttachment : IChatAttachment
{
    /// <summary>Absolute path, as the clipboard reported it.</summary>
    public string FullPath { get; }

    /// <summary>File or folder name, shown on the chip above the input box.</summary>
    public string DisplayName { get; }

    public ChatFileAttachment(string fullPath)
    {
        FullPath = string.IsNullOrWhiteSpace(fullPath)
            ? throw new ArgumentException("Path is required.", nameof(fullPath))
            : fullPath;

        // A folder copied out of Explorer arrives without a trailing separator,
        // but a drive root ("R:\") keeps one and has no name to show.
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        DisplayName = string.IsNullOrEmpty(name) ? fullPath : name;
    }
}
