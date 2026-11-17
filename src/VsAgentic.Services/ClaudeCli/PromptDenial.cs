using System;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// A tool call the CLI denied because it would have waited for the user's
/// approval, with <c>--permission-prompts none</c> (see
/// <see cref="Configuration.VsAgenticOptions.DenyPermissionPrompts"/>). The text
/// of the tool result is written for the model (CLI 2.1.325):
/// <code>Permission for this tool use was denied. It requires approval, and this session has no approval surface — nobody can answer … What required approval: Claude requested permissions to write to C:\…\a.txt, but you haven't granted it yet.</code>
/// The CLI also sends a <c>system/permission_denied</c> event for it, but the
/// text is what the step already has, in a subagent as well.
/// </summary>
public static class PromptDenial
{
    private const string Prefix = "Permission for this tool use was denied. It requires approval, and this session has no approval surface";

    public const string Label = "Not run · would need approval";

    public static bool IsDenial(string? toolResultText) =>
        toolResultText is not null && toolResultText.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The line the step shows above its input.</summary>
    public static string BodyLine => $"**{Label}**";

    /// <summary>
    /// Sent to the model with the first message after the switch is turned off.
    /// The denial text above tells it that such calls "will be denied the same
    /// way for the rest of this session", and that stays in the conversation:
    /// without this note the model does not try again, also when asked to
    /// (measured on 2.1.325; a note in the system prompt was not enough).
    /// </summary>
    public const string PromptsBackNote =
        "Permission prompts are back on in this session: a tool call that needs approval now asks the user, "
        + "who can allow it. Earlier tool results said such calls would be denied for the rest of the session; "
        + "that no longer holds. When the user asks for one of them again, make the call.";
}
