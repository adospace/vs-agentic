using System;

namespace VsAgentic.Services.Configuration;

/// <summary>
/// The subscription the user is on. This exists only to size the rate-limit
/// meter in the chat header — nothing here is sent to the CLI, and picking the
/// wrong one changes no behaviour beyond how full that meter looks.
/// </summary>
public enum ClaudeUsagePlan
{
    Pro,
    Max5x,
    Max20x,

    /// <summary>
    /// No rolling cap worth drawing. Hides the 5-hour and weekly meters for
    /// anyone who would rather not see an estimate at all.
    /// </summary>
    Unlimited,
}

/// <summary>
/// Token budgets for the rolling usage windows.
///
/// IMPORTANT: Anthropic does not publish the token budget behind the 5-hour and
/// weekly limits — the real limits are enforced server-side and vary with model
/// and load. The numbers here are deliberate order-of-magnitude estimates so
/// the meter conveys "barely started" vs "nearly out", and they are overridable
/// via <see cref="VsAgenticOptions.FiveHourTokenBudget"/> and
/// <see cref="VsAgenticOptions.WeeklyTokenBudget"/>. Treat the meter as a
/// gauge, not as an authority — the CLI remains the only thing that knows when
/// you have actually run out.
/// </summary>
public static class ClaudeUsagePlanDefaults
{
    public static readonly TimeSpan ShortWindow = TimeSpan.FromHours(5);
    public static readonly TimeSpan LongWindow = TimeSpan.FromDays(7);

    public static long ShortWindowTokens(ClaudeUsagePlan plan) => plan switch
    {
        ClaudeUsagePlan.Pro => 250_000,
        ClaudeUsagePlan.Max5x => 1_250_000,
        ClaudeUsagePlan.Max20x => 5_000_000,
        _ => 0,
    };

    public static long LongWindowTokens(ClaudeUsagePlan plan) => plan switch
    {
        ClaudeUsagePlan.Pro => 2_500_000,
        ClaudeUsagePlan.Max5x => 12_500_000,
        ClaudeUsagePlan.Max20x => 50_000_000,
        _ => 0,
    };

    public static string ToDisplayName(this ClaudeUsagePlan plan) => plan switch
    {
        ClaudeUsagePlan.Pro => "Pro",
        ClaudeUsagePlan.Max5x => "Max 5x",
        ClaudeUsagePlan.Max20x => "Max 20x",
        _ => "Unlimited",
    };
}
