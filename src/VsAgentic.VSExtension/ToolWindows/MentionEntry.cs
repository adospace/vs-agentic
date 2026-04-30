using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace VsAgentic.VSExtension.ToolWindows;

/// <summary>One entry shown in the @-mention file/folder picker popup.</summary>
public sealed class MentionEntry
{
    public string RelativePath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    public ImageMoniker Moniker => IsDirectory
        ? KnownMonikers.FolderClosed
        : KnownMonikers.Document;

    public MentionEntry(string relativePath, string name, bool isDirectory)
    {
        RelativePath = relativePath;
        Name = name;
        IsDirectory = isDirectory;
    }
}
