namespace VsAgentic.Services.IdeContext;

/// <summary>
/// The tool that shows review findings in the Error List. The host adds it
/// when it has an Error List; the chat shows the Review button only then.
/// </summary>
public static class ReviewTool
{
    public const string Name = "report_review_findings";

    /// <summary>The message the Review button sends.</summary>
    public const string Request =
        "Review the uncommitted changes in this repository: staged, unstaged and new files. " +
        "Look for bugs, security problems and missed cases. Show all findings with the " +
        Name + " tool in one call, or with an empty list when there are none. " +
        "Then give a short summary.";
}
