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
    [Description("Controls how the CLI handles tool permissions. Default: every gated tool call surfaces an Allow/Deny banner in the chat (safest). AcceptEdits: file edits (Edit, Write, NotebookEdit) auto-accept; everything else still prompts. BypassPermissions: auto-accept every tool call without prompting (use only in trusted environments).")]
    [DefaultValue(CliPermissionMode.Default)]
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.Default;

    [Category("Claude CLI")]
    [DisplayName("Model")]
    [Description("Model alias passed to the CLI (opus, sonnet, haiku, fable), or empty to leave the CLI's own choice alone. Normally set from the dropdown in the chat header; this is where that choice is remembered between restarts.")]
    [DefaultValue("")]
    public string Model { get; set; } = "";

    [Category("Claude CLI")]
    [DisplayName("Reasoning effort")]
    [Description("Effort level passed to the CLI on every session. Always sent, because the CLI does not report the effort it is running with — sending it is the only way the header can name a level that is genuinely in force. Note this overrides an effort configured elsewhere in your own CLI settings. Normally set from the dropdown in the chat header.")]
    [DefaultValue(ClaudeEffort.High)]
    public ClaudeEffort Effort { get; set; } = ClaudeEffort.High;

    [Category("Usage meters")]
    [DisplayName("Plan")]
    [Description("Subscription the 5-hour and weekly meters in the chat header are sized against. Display only — nothing here is sent to the CLI. Choose Unlimited to hide those meters.")]
    [DefaultValue(ClaudeUsagePlan.Pro)]
    public ClaudeUsagePlan UsagePlan { get; set; } = ClaudeUsagePlan.Pro;

    [Category("Usage meters")]
    [DisplayName("5-hour token budget")]
    [Description("Overrides the 5-hour budget implied by the plan. 0 keeps the plan default. Anthropic does not publish the real limit, so the built-in numbers are estimates — set this if you have measured your own.")]
    [DefaultValue(0L)]
    public long FiveHourTokenBudget { get; set; }

    [Category("Usage meters")]
    [DisplayName("Weekly token budget")]
    [Description("Overrides the weekly budget implied by the plan. 0 keeps the plan default.")]
    [DefaultValue(0L)]
    public long WeeklyTokenBudget { get; set; }

    [Category("Sessions")]
    [DisplayName("Keep days of activity")]
    [Description("When the extension starts, sessions whose last activity is older than this many days are deleted. Default: 30. Set to 0 to disable cleanup.")]
    [DefaultValue(30)]
    public int KeepActivityDays { get; set; } = 30;
}
