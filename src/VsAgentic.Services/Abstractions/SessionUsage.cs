using System;
using System.Collections.Generic;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// How alarming a meter reading is. Kept as a named level rather than a raw
/// fraction so the thresholds live in one place and the XAML can switch colours
/// on a trigger instead of arithmetic.
/// </summary>
public enum UsageLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// What a session has spent, as of one moment. Snapshots are handed to the UI
/// whole rather than as a set of mutable counters, so the status bar can never
/// render a half-updated row (e.g. new session totals against stale windows).
///
/// Three different things are counted here and they are not interchangeable:
///  - <see cref="TotalTokens"/> is everything this session has ever sent or
///    received. It only grows.
///  - <see cref="ContextTokens"/> is how full the model's context window was on
///    the last call. It falls back down after a compaction.
///  - The rolling windows are machine-wide, span every session and survive a
///    restart, because that is the scope the rate limit itself applies at.
///    They are the fallback: once the CLI reports the plan limits
///    (<see cref="ShortWindowLimit"/>, <see cref="LongWindowLimit"/>), those
///    are shown instead.
/// </summary>
public record SessionUsage
{
    public static readonly SessionUsage Empty = new();

    // ── Session totals ────────────────────────────────────────────────────

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheCreationTokens { get; init; }

    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

    /// <summary>Cumulative USD, as reported by the CLI. Null until the first result event.</summary>
    public decimal? CostUsd { get; init; }

    // ── Context window ────────────────────────────────────────────────────

    /// <summary>
    /// Tokens the last API call carried into the model: fresh input, plus both
    /// halves of the cache, plus what came back out. This is the number that has
    /// to stay under the model's context window.
    ///
    /// The window itself is not part of this snapshot. It follows the model, and
    /// the model has its own source (<see cref="IChatService.CurrentModel"/>,
    /// or a preview of it before the first turn), so the host pairs the two.
    /// </summary>
    public long ContextTokens { get; init; }

    // ── Rolling rate-limit windows ────────────────────────────────────────

    /// <summary>Tokens spent in the trailing 5 hours, across every session.</summary>
    public long ShortWindowTokens { get; init; }

    /// <summary>Tokens spent in the trailing 7 days, across every session.</summary>
    public long LongWindowTokens { get; init; }

    /// <summary>Budget the short window is measured against; 0 means "no cap known", and the meter hides.</summary>
    public long ShortWindowBudget { get; init; }

    /// <summary>Budget the long window is measured against; 0 means "no cap known", and the meter hides.</summary>
    public long LongWindowBudget { get; init; }

    public double ShortWindowFraction => Fraction(ShortWindowTokens, ShortWindowBudget);

    public double LongWindowFraction => Fraction(LongWindowTokens, LongWindowBudget);

    public bool HasWindowBudget => ShortWindowBudget > 0 || LongWindowBudget > 0;

    public bool HasAny => TotalTokens > 0;

    // ── Plan limits reported by the CLI ───────────────────────────────────
    // When present, these replace the local estimate for their window: they
    // are the server's reading for the whole account. Null when the CLI has
    // not reported yet, cannot (an older CLI), or the window has reset since.

    /// <summary>The 5-hour plan window, as the CLI last reported it.</summary>
    public PlanLimitWindow? ShortWindowLimit { get; init; }

    /// <summary>The weekly plan window, as the CLI last reported it.</summary>
    public PlanLimitWindow? LongWindowLimit { get; init; }

    /// <summary>Weekly windows that apply to one model only, e.g. Fable.</summary>
    public IReadOnlyList<PlanLimitWindow> ModelLimits { get; init; } = Array.Empty<PlanLimitWindow>();

    /// <summary>
    /// Only a reported limit warns. The estimate has no colour: its budget is a
    /// guess, and a guess should not turn the status bar red.
    /// </summary>
    public UsageLevel ShortWindowLevel => ShortWindowLimit is { } l ? LevelOf(l.Fraction) : UsageLevel.Normal;

    public UsageLevel LongWindowLevel => LongWindowLimit is { } l ? LevelOf(l.Fraction) : UsageLevel.Normal;

    /// <summary>
    /// Warn at three quarters, escalate at nine tenths. The warning band is
    /// wide on purpose: the point is to be told while there is still room to
    /// change course, not once the decision has been made for you.
    /// </summary>
    public static UsageLevel LevelOf(double fraction) =>
        fraction >= 0.90 ? UsageLevel.Critical :
        fraction >= 0.75 ? UsageLevel.Warning :
        UsageLevel.Normal;

    /// <summary>
    /// Clamped so a meter can never render past its track. Over-budget is a
    /// state we expect to see: the budgets are estimates, so going past one
    /// says the estimate was low, not that usage is impossible.
    /// </summary>
    public static double Fraction(long used, long budget) =>
        budget <= 0 ? 0d : Math.Min(1d, Math.Max(0d, (double)used / budget));
}
