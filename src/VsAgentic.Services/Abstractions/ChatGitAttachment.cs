namespace VsAgentic.Services.Abstractions;

public enum GitReferenceKind
{
    Commit,
    Branch,
    PullRequest,
}

/// <summary>
/// A commit, the changes of a branch, or a pull request, attached to the next
/// message. Like a file, only the reference goes out: the message names it and
/// says which command shows it, and the agent runs that command when it needs
/// the changes. A large pull request costs nothing until it is read.
/// </summary>
public sealed class ChatGitAttachment : IChatAttachment
{
    public GitReferenceKind Kind { get; }

    /// <summary>
    /// What git or gh needs to show the changes: the full commit hash, the
    /// branch the changes are compared against, or the pull request number.
    /// </summary>
    public string Id { get; }

    /// <summary>The commit subject, the branch name, or the pull request title.</summary>
    public string Title { get; }

    /// <summary>Changed files, lines added and removed; null when not known.</summary>
    public int? FileCount { get; }
    public int? Added { get; }
    public int? Removed { get; }

    /// <summary>"Commit 1a2b3c4", "feature/x vs main" or "PR #42".</summary>
    public string DisplayName { get; }

    /// <summary>"3 files +10 −2", or empty when the size is not known.</summary>
    public string Scope { get; }

    /// <summary>
    /// Not a path, but the same commit, branch or pull request attached twice
    /// is one attachment, and this is what the pending list compares.
    /// </summary>
    public string? SourcePath => Kind switch
    {
        GitReferenceKind.Commit => "commit " + Id,
        GitReferenceKind.Branch => "branch " + Title + " vs " + Id,
        _ => "pull request #" + Id,
    };

    /// <summary>The line the message carries for the agent.</summary>
    public string PromptLine { get; }

    private ChatGitAttachment(
        GitReferenceKind kind, string id, string title, string displayName, string promptLine,
        int? fileCount, int? added, int? removed)
    {
        Kind = kind;
        Id = id;
        Title = title;
        DisplayName = displayName;
        PromptLine = promptLine;
        FileCount = fileCount;
        Added = added;
        Removed = removed;
        Scope = FormatScope(fileCount, added, removed);
    }

    public static ChatGitAttachment Commit(
        string hash, string subject, int? fileCount = null, int? added = null, int? removed = null)
    {
        var shortHash = hash.Length > 7 ? hash.Substring(0, 7) : hash;
        return new ChatGitAttachment(
            GitReferenceKind.Commit, hash, subject,
            "Commit " + shortHash,
            $"Attached commit `{shortHash}` ({subject}); read it with `git show {shortHash}`.",
            fileCount, added, removed);
    }

    /// <param name="branch">The branch with the changes, as git names it.</param>
    /// <param name="baseRef">What it is compared against, e.g. <c>origin/main</c>.</param>
    public static ChatGitAttachment Branch(
        string branch, string baseRef, int? fileCount = null, int? added = null, int? removed = null)
    {
        return new ChatGitAttachment(
            GitReferenceKind.Branch, baseRef, branch,
            branch + " vs " + ShortBase(baseRef),
            $"Attached the changes of branch `{branch}` against `{baseRef}`; " +
            $"read them with `git diff {baseRef}...{branch}` and `git log {baseRef}..{branch}`.",
            fileCount, added, removed);
    }

    /// <param name="title">Null when the pull request could not be looked up.</param>
    public static ChatGitAttachment PullRequest(
        int number, string? title, int? fileCount = null, int? added = null, int? removed = null)
    {
        var named = string.IsNullOrWhiteSpace(title) ? "" : $" ({title})";
        return new ChatGitAttachment(
            GitReferenceKind.PullRequest, number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title ?? "",
            "PR #" + number,
            $"Attached pull request #{number}{named}; read it with `gh pr view {number}` and `gh pr diff {number}`.",
            fileCount, added, removed);
    }

    /// <summary>"origin/main" reads as "main" on a chip; the message keeps the full name.</summary>
    private static string ShortBase(string baseRef)
    {
        var slash = baseRef.IndexOf('/');
        return baseRef.StartsWith("origin/", StringComparison.Ordinal) && slash >= 0
            ? baseRef.Substring(slash + 1)
            : baseRef;
    }

    internal static string FormatScope(int? fileCount, int? added, int? removed)
    {
        if (fileCount is null) return "";
        var files = fileCount == 1 ? "1 file" : $"{fileCount} files";
        return $"{files} +{added ?? 0} −{removed ?? 0}";
    }
}
