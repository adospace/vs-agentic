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
    /// <summary>
    /// Raised after the user clicks OK/Apply. Already-open chat sessions hold
    /// their own copy of the options captured when their tool window was
    /// created, so the package needs this to push changes (e.g. the Appearance
    /// toggles) into them live instead of waiting for the next session.
    /// </summary>
    public event EventHandler? Applied;

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        if (e.ApplyBehavior == ApplyKind.Apply)
            Applied?.Invoke(this, EventArgs.Empty);
    }

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
    [Description("Model alias passed to the CLI (opus, sonnet, haiku, fable), or empty to leave the CLI's own choice alone. Normally set from the dropdown under the chat input; this is where that choice is remembered between restarts.")]
    [DefaultValue("")]
    public string Model { get; set; } = "";

    [Category("Claude CLI")]
    [DisplayName("Reasoning effort")]
    [Description("Effort level passed to the CLI. Default sends no --effort flag, so the CLI uses its own setting and an older CLI without that flag still starts. The CLI does not report the effort it runs with, so the dropdown then shows Default rather than a level. Choosing a level overrides an effort configured in your own CLI settings. Normally set from the dropdown under the chat input.")]
    [DefaultValue(ClaudeEffort.Default)]
    public ClaudeEffort Effort { get; set; } = ClaudeEffort.Default;

    [Category("Claude CLI")]
    [DisplayName("Background command limit")]
    [Description("Most time Claude may give a background shell command (a dev server, a watcher) before the CLI stops it. The CLI treats this extension as an unattended session and stops such commands after 30 minutes unless Claude asks for longer, and allows at most 2 hours. This setting raises that maximum through BASH_MAX_TIMEOUT_MS; the 30-minute default stays. CliDefault leaves the variable alone. A value already set in the environment Visual Studio started with is kept. Applies to sessions started after the change.")]
    [DefaultValue(BackgroundCommandLimit.OneDay)]
    public BackgroundCommandLimit BackgroundCommandLimit { get; set; } = BackgroundCommandLimit.OneDay;

    [Category("Claude CLI")]
    [DisplayName("Check for CLI updates")]
    [Description("On startup, compare the Claude Code CLI against the latest release published on the npm registry and show a banner when a newer one exists. The banner offers to run 'claude update' in a console window. Nothing is installed without a click.")]
    [DefaultValue(true)]
    public bool CheckForCliUpdates { get; set; } = true;

    [Category("Usage meters")]
    [DisplayName("Plan")]
    [Description("Subscription the 5-hour and weekly readings under the chat input are sized against. Display only — nothing here is sent to the CLI. Choose Unlimited to hide those readings.")]
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

    [Category("Appearance")]
    [DisplayName("Zoom (%)")]
    [Description("Size of the chat text and the controls around it, as a percentage. Normally set with Ctrl+mouse wheel or Ctrl+Plus/Minus in the chat window (Ctrl+0 restores 100%); this is where that choice is remembered between restarts. Values are snapped to the nearest zoom step, between 50 and 300.")]
    [DefaultValue(100)]
    public int ZoomPercent { get; set; } = 100;

    [Category("Appearance")]
    [DisplayName("Working - animate tab title")]
    [Description("Show a spinner on chat window's tab title while a turn is running. Turn off if you prefer a static title.")]
    [DefaultValue(true)]
    public bool AnimateTitleWhileBusy { get; set; } = true;

    [Category("Appearance")]
    [DisplayName("Waiting for input - animate tab title")]
    [Description("Make the waiting for input more visible by showing a waving hand on chat window's tab title. Applies to input from you, such as permission or question banner. Also shows when window is backgrounded.")]
    [DefaultValue(true)]
    public bool AnimateTitleWhileWaiting { get; set; } = true;

    [Category("Appearance")]
    [DisplayName("Waiting for input - flash status bar")]
    [Description("Pulse the background of the bar under the chat input while a permission or question banner needs an answer.")]
    [DefaultValue(true)]
    public bool FlashStatusBarWhileWaiting { get; set; } = true;

    // Property name kept from when this switched a tab icon rather than a title
    // marker: DialogPage persists by property name, so renaming it would quietly
    // reset anyone who had turned it off.
    [Category("Appearance")]
    [DisplayName("Completed - show title marker")]
    [Description("Show a sparkle on the chat window's tab title once a turn finishes, until you switch to that tab. Also shows when the window is backgrounded. Turn off to leave the title alone when a turn ends.")]
    [DefaultValue(true)]
    public bool ShowCompletedIndicator { get; set; } = true;

    [Category("Appearance")]
    [DisplayName("Completed - animate tab title")]
    [Description("Pulse the completed marker rather than showing it still. Has no effect unless 'Completed - show title marker' is on.")]
    [DefaultValue(true)]
    public bool AnimateTitleWhenComplete { get; set; } = true;

    [Category("Appearance")]
    [DisplayName("Completed - flash status bar")]
    [Description("Pulse the background of the bar under the chat input once a turn finishes, until you switch to that tab.")]
    [DefaultValue(true)]
    public bool FlashStatusBarWhenComplete { get; set; } = true;

    [Category("Editor context")]
    [DisplayName("Suggest the active file and selection")]
    [Description("Offer the file open in the editor, and the lines selected in it, as a faded chip above the chat input. Nothing is sent until you click the chip; it then goes out with the next message like an attached file. Selected text is sent as well, so unsaved changes reach Claude too.")]
    [DefaultValue(true)]
    public bool SuggestEditorContext { get; set; } = true;

    [Category("Sessions")]
    [DisplayName("Keep days of activity")]
    [Description("When the extension starts, a session whose last activity is older than this many days is archived, and a session that has been in the archive for this long is deleted. A session is therefore kept for twice this many days before anything is lost. Restoring a session counts as activity. The Claude CLI removes its own copy of a conversation after its cleanupPeriodDays setting (default 30). When it no longer has a session's conversation, the next message in that session starts a new one, and Claude does not see the earlier messages. Default: 30. Set to 0 to disable cleanup.")]
    [DefaultValue(30)]
    public int KeepActivityDays { get; set; } = 30;
}
