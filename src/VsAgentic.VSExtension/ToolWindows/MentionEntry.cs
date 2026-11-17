using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.VSExtension.ToolWindows;

/// <summary>
/// One entry shown in the @-mention popup: a file or folder, whose path goes
/// into the message text, or a commit, branch or pull request, which becomes
/// an attachment chip.
/// </summary>
public sealed class MentionEntry
{
    public string RelativePath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    /// <summary>Set for a commit, branch or pull request; null for a path.</summary>
    public ChatGitAttachment? Git { get; }

    /// <summary>
    /// A pull request typed by number that is not among the open ones listed.
    /// Looked up when picked, so that typing a number does not query GitHub on
    /// every keystroke.
    /// </summary>
    public bool NeedsLookup { get; }

    /// <summary>The main text of the row.</summary>
    public string Text { get; }

    /// <summary>Grey text after it: the commit subject, the size, or empty.</summary>
    public string Detail { get; }

    /// <summary>What the typed filter is matched against.</summary>
    public string SearchText { get; }

    public ImageMoniker Moniker => Git?.Kind switch
    {
        GitReferenceKind.Commit => KnownMonikers.Commit,
        GitReferenceKind.Branch => KnownMonikers.Branch,
        GitReferenceKind.PullRequest => KnownMonikers.PullRequest,
        _ => IsDirectory ? KnownMonikers.FolderClosed : KnownMonikers.Document,
    };

    public MentionEntry(string relativePath, string name, bool isDirectory)
    {
        RelativePath = relativePath;
        Name = name;
        IsDirectory = isDirectory;
        Text = relativePath;
        Detail = "";
        SearchText = relativePath;
    }

    public MentionEntry(ChatGitAttachment git, bool needsLookup = false)
    {
        Git = git;
        NeedsLookup = needsLookup;
        RelativePath = git.DisplayName;
        Name = git.DisplayName;

        switch (git.Kind)
        {
            case GitReferenceKind.Commit:
                Text = git.DisplayName;
                Detail = Join(git.Title, git.Scope);
                // "commit" lets the user ask for the list by kind.
                SearchText = $"commit {git.Id} {git.Title}";
                break;
            case GitReferenceKind.Branch:
                Text = "Branch changes: " + git.DisplayName;
                Detail = git.Scope;
                SearchText = $"branch changes diff {git.Title} {git.Id}";
                break;
            default:
                Text = git.DisplayName;
                Detail = needsLookup ? "pull request" : Join(git.Title, git.Scope);
                SearchText = $"pr pull request #{git.Id} {git.Title}";
                break;
        }
    }

    private static string Join(string a, string b) =>
        string.IsNullOrEmpty(b) ? a : string.IsNullOrEmpty(a) ? b : a + " · " + b;
}
