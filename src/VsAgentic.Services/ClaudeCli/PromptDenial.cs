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
}
