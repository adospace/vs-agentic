using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Configuration;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// Options page shown under Tools → Options → VsAgentic → General.
/// Settings are automatically persisted to the VS registry by DialogPage.
/// </summary>
[ComVisible(true)]
[Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")]
public class VsAgenticOptionsPage : DialogPage
{
    [Category("Claude CLI")]
    [DisplayName("Claude CLI Path")]
    [Description("Path to the Claude Code CLI executable. Defaults to 'claude' (assumes it's on PATH).")]
    [DefaultValue("claude")]
    public string ClaudeCliPath { get; set; } = "claude";

    [Category("Claude CLI")]
    [DisplayName("CLI Permission Mode")]
    [Description("Controls how the CLI handles tool permissions. AcceptEdits: auto-accepts file edits (recommended). BypassPermissions: skips all checks (use in trusted environments only). Default: prompts for all permissions (not recommended — CLI runs non-interactively).")]
    [DefaultValue(CliPermissionMode.AcceptEdits)]
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.AcceptEdits;
}
