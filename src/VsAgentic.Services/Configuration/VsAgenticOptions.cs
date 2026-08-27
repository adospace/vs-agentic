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
    /// choice alone. Set from the model dropdown in the chat header; see
    /// <see cref="ClaudeModelCatalog"/> for the accepted values.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Reasoning effort passed to the CLI's <c>--effort</c>, always sent so the
    /// header can show a value that is actually in force. Set from the effort
    /// dropdown in the chat header.
    /// </summary>
    public ClaudeEffort Effort { get; set; } = ClaudeEffort.High;

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
}
