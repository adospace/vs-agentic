using System;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// A tool call that the auto mode classifier blocked. The CLI tells the host
/// only through the text of the tool result: the <c>permission_denied</c>
/// event, which carries the reason on its own, is not sent when the permission
/// prompt is an MCP tool (measured on 2.1.319). The text is written for the
/// model:
/// <code>Permission for this action was denied by the Claude Code auto mode classifier. Reason: [Code from External]. If you have other tasks …</code>
/// The CLI's own terminal reads the reason out of it the same way.
/// </summary>
public static class AutoModeBlock
{
    private const string Prefix = "Permission for this action was denied by the Claude Code auto mode classifier. Reason: ";

    // Where the CLI's advice to the model starts. Searched from the end, as
    // the CLI does: the reason is the classifier's text and may contain it.
    private const string AdviceStart = ". If you have other tasks";

    public const string Label = "Blocked by auto mode";

    /// <summary>
    /// The classifier's reason, if <paramref name="toolResultText"/> is the
    /// text of a blocked call. Empty when the classifier gave none.
    /// </summary>
    public static bool TryReadReason(string? toolResultText, out string reason)
    {
        reason = "";
        if (toolResultText is null || !toolResultText.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var rest = toolResultText.Substring(Prefix.Length);
        var end = rest.LastIndexOf(AdviceStart, StringComparison.Ordinal);
        reason = (end >= 0 ? rest.Substring(0, end) : rest).Trim();
        return true;
    }

    /// <summary>
    /// The line the step shows above its input: <c>**Blocked by auto mode:** reason</c>.
    /// </summary>
    public static string BodyLine(string reason) =>
        reason.Length == 0 ? $"**{Label}**" : $"**{Label}:** {Services.MarkdownText.Escape(reason)}";
}
