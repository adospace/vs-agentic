namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// The messages a plan is sent back with. The CLI returns a denied plan to
/// Claude as an error result whose text is the message; a plan denied because
/// the turn was stopped comes back as an error too. The text tells the two
/// apart, so the plan card does not say "sent back" after a Stop.
/// </summary>
public static class PlanAnswers
{
    private const string KeepPlanningPrefix =
        "The user did not approve the plan and wants you to keep planning.";

    public static string KeepPlanning(string? feedback)
    {
        var text = (feedback ?? "").Trim();
        return text.Length > 0
            ? KeepPlanningPrefix + " Their feedback: " + text
            : KeepPlanningPrefix + " Ask them what to change.";
    }

    /// <summary>Whether a plan's result is the user sending it back.</summary>
    public static bool IsKeepPlanning(string? resultText) =>
        resultText is not null && resultText.Contains(KeepPlanningPrefix);
}
