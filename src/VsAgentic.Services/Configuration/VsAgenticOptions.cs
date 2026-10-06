namespace VsAgentic.Services.Configuration;

public class VsAgenticOptions
{
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// Path to the Claude CLI executable. Defaults to "claude" (assumes it's on PATH).
    /// </summary>
    public string ClaudeCliPath { get; set; } = "claude";

    /// <summary>
    /// Permission mode for the Claude CLI. Controls how tool permissions are handled.
    /// Defaults to <see cref="CliPermissionMode.Default"/>: every gated tool call is
    /// surfaced to the user via the in-process MCP permission helper, and the user
    /// approves/denies it through the chat banner. Use <see cref="CliPermissionMode.AcceptEdits"/>
    /// or <see cref="CliPermissionMode.BypassPermissions"/> as escape hatches.
    /// </summary>
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.Default;

    /// <summary>
    /// Alias passed to the CLI's <c>--model</c>, or empty to leave the CLI's own
    /// choice alone. Set from the model dropdown in the chat status bar; see
    /// <see cref="ClaudeModelCatalog"/> for the accepted values.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Reasoning effort passed to the CLI's <c>--effort</c>. <see cref="ClaudeEffort.Default"/>
    /// sends no flag, so older CLIs still start. Set from the effort dropdown in
    /// the chat status bar.
    /// </summary>
    public ClaudeEffort Effort { get; set; } = ClaudeEffort.Default;

    /// <summary>
    /// Subscription the rolling usage meters are sized against. Display only —
    /// nothing here reaches the CLI.
    /// </summary>
    public ClaudeUsagePlan UsagePlan { get; set; } = ClaudeUsagePlan.Pro;

    /// <summary>
    /// Overrides the 5-hour token budget implied by <see cref="UsagePlan"/>.
    /// Zero keeps the plan default. This exists because the real limit is not
    /// published and varies — see <see cref="ClaudeUsagePlanDefaults"/>.
    /// </summary>
    public long FiveHourTokenBudget { get; set; }

    /// <summary>
    /// Overrides the weekly token budget implied by <see cref="UsagePlan"/>.
    /// Zero keeps the plan default.
    /// </summary>
    public long WeeklyTokenBudget { get; set; }

    /// <summary>The 5-hour budget actually in force, after any override.</summary>
    public long EffectiveFiveHourBudget =>
        FiveHourTokenBudget > 0 ? FiveHourTokenBudget : ClaudeUsagePlanDefaults.ShortWindowTokens(UsagePlan);

    /// <summary>The weekly budget actually in force, after any override.</summary>
    public long EffectiveWeeklyBudget =>
        WeeklyTokenBudget > 0 ? WeeklyTokenBudget : ClaudeUsagePlanDefaults.LongWindowTokens(UsagePlan);

    /// <summary>
    /// Animate the tool window caption with a spinner while a turn is running.
    /// Disabling leaves the plain session title.
    /// </summary>
    public bool AnimateTitleWhileBusy { get; set; } = true;

    /// <summary>
    /// Animate the tool window caption with a waving hand while a permission or
    /// question banner is waiting to be answered.
    /// </summary>
    public bool AnimateTitleWhileWaiting { get; set; } = true;

    /// <summary>
    /// Pulse the chat status bar background while a banner is waiting to be
    /// answered. Separate from <see cref="AnimateTitleWhileWaiting"/> because the
    /// caption is what a backgrounded window shows, while the flash only helps
    /// once the window is already on screen.
    /// </summary>
    public bool FlashStatusBarWhileWaiting { get; set; } = true;

    /// <summary>
    /// Mark the tool window caption with a sparkle once a turn finishes, until
    /// the window is looked at. Gates both forms of the marker: with this off,
    /// <see cref="AnimateTitleWhenComplete"/> has nothing to animate.
    /// </summary>
    public bool ShowCompletedIndicator { get; set; } = true;

    /// <summary>
    /// Pulse the completed marker rather than showing it still. The Completed
    /// counterpart of <see cref="AnimateTitleWhileBusy"/>, except that Busy and
    /// AwaitingUser have no separate switch for whether the marker shows at all.
    /// </summary>
    public bool AnimateTitleWhenComplete { get; set; } = true;

    /// <summary>
    /// Pulse the chat status bar background once a turn finishes, until the
    /// window is looked at. Separate from <see cref="AnimateTitleWhenComplete"/>
    /// for the same reason as <see cref="FlashStatusBarWhileWaiting"/>: the
    /// caption is what a backgrounded window shows, while the flash only helps
    /// once the window is already on screen.
    /// </summary>
    public bool FlashStatusBarWhenComplete { get; set; } = true;
}
