using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Agents;

/// <summary>Cost, time and tokens of one way to do a task.</summary>
public sealed class SplitFigures
{
    public decimal CostUsd { get; set; }
    public double Seconds { get; set; }
    public long Tokens { get; set; }
}

/// <summary>What a plan would cost alone and with agents, before it runs.</summary>
public sealed class SplitEstimate
{
    public SplitFigures Alone { get; set; } = new SplitFigures();
    public SplitFigures Agents { get; set; } = new SplitFigures();

    /// <summary>Per part, in the order of the plan.</summary>
    public List<SplitFigures> Parts { get; set; } = new List<SplitFigures>();

    /// <summary>Earlier runs the figures were corrected by; 0 when none.</summary>
    public int CalibrationRuns { get; set; }

    /// <summary>The corrections applied to the figures above; 1 when none.</summary>
    public double CostFactor { get; set; } = 1;
    public double TimeFactor { get; set; } = 1;

    /// <summary>
    /// What a run of this plan records: the figures before the correction,
    /// so the next correction is measured against the model itself and not
    /// against its own earlier correction.
    /// </summary>
    public SplitOutcome OutcomeFor(decimal actualCost, double actualSeconds) => new SplitOutcome
    {
        EstimatedCost = Agents.CostUsd / (decimal)CostFactor,
        ActualCost = actualCost,
        EstimatedAloneCost = Alone.CostUsd / (decimal)CostFactor,
        EstimatedSeconds = Agents.Seconds / TimeFactor,
        ActualSeconds = actualSeconds,
        EstimatedAloneSeconds = Alone.Seconds / TimeFactor,
    };

    /// <summary>The chat's size was not known, so "alone" leaves out re-reading it.</summary>
    public bool ContextUnknown { get; set; }
}

/// <summary>What the session knows when the card asks for an estimate.</summary>
public sealed class SplitEstimateInput
{
    /// <summary>The main chat's model: it works alone, and it orchestrates.</summary>
    public string? MainModel { get; set; }

    /// <summary>How full the main chat's context is, in tokens; 0 when not known.</summary>
    public long ContextTokens { get; set; }

    public bool ContextUnknown { get; set; }
}

/// <summary>
/// A rough estimate of a split plan, from the planner's size of each part,
/// the chat's measured context and the price list. Labelled as an estimate on
/// the card; the summary after a run shows it next to the measured figures,
/// and those correct the next estimate (<see cref="SplitCalibration"/>).
///
/// The model behind it: an agent re-reads its whole context (from the cache)
/// on each step, so the cost of a step grows with the context it carries.
/// Working alone, every step carries the main chat's context; an agent starts
/// small and carries only its own part, but reads some of what the main chat
/// already knows again (+15 %), and the main chat still spends a few steps
/// handing out the parts and reading the results.
/// </summary>
public static class AgentSplitEstimator
{
    /// <summary>What an agent reads again that the main chat already had.</summary>
    public const double RereadShare = 0.15;

    // A subagent's starting context: system prompt, tools and its task.
    private const long AgentBaseContext = 15_000;

    // Of the tokens a part handles, the share the model writes; the rest it reads.
    private const double OutputShare = 0.15;

    // Tool run, network and first token, per step.
    private const double SecondsPerStep = 4;

    // The main chat per part: the Agent call it writes and the report it reads.
    // Measured 12. 11. on four read-only parts: reports of 5–10k each, and
    // the main chat read again what it wanted to trust.
    private const long OrchestratorTokensPerPart = 8_000;

    // A new chat is not empty: Claude Code's system prompt and tools.
    private const long MainBaseContext = 25_000;

    /// <summary>Parts the planner gave no size get a size by role.</summary>
    public static (long Tokens, int Steps) DefaultSize(AgentSplitTask part)
    {
        var r = (part.Role ?? "").ToLowerInvariant();
        if (r.Contains("explor") || r.Contains("search")) return (30_000, 10);
        if (r.Contains("review")) return (25_000, 6);
        if (r.Contains("test")) return (15_000, 4);
        return part.Writes ? (25_000, 8) : (20_000, 6);
    }

    public static (long Tokens, int Steps) SizeOf(AgentSplitTask part)
    {
        var d = DefaultSize(part);
        var tokens = part.EstimatedTokens > 0 ? Math.Min(part.EstimatedTokens, 2_000_000) : d.Tokens;
        var steps = part.EstimatedSteps > 0 ? Math.Min(part.EstimatedSteps, 200) : d.Steps;
        return (tokens, steps);
    }

    public static SplitEstimate Estimate(IReadOnlyList<AgentSplitTask> parts, SplitEstimateInput input,
        ModelPriceList prices, SplitCalibration? calibration)
    {
        var estimate = new SplitEstimate { ContextUnknown = input.ContextUnknown };
        if (parts.Count == 0) return estimate;

        var main = prices.For(input.MainModel);
        var context = Math.Max(MainBaseContext, input.ContextTokens);

        // Alone: the main chat does every part, carrying its context each step.
        long work = 0;
        int steps = 0;
        foreach (var p in parts)
        {
            var size = SizeOf(p);
            work += size.Tokens;
            steps += size.Steps;
        }
        estimate.Alone = Run(main, context, work, steps, startCached: true);

        // Agents: each part on its own model from a small context, a stage at
        // a time; parts in one stage run side by side.
        var agents = new SplitFigures();
        var stageSeconds = new Dictionary<int, double>();
        foreach (var p in parts)
        {
            var size = SizeOf(p);
            var figures = Run(prices.For(p.Model), AgentBaseContext, (long)(size.Tokens * (1 + RereadShare)), size.Steps, startCached: false);
            estimate.Parts.Add(figures);
            agents.CostUsd += figures.CostUsd;
            agents.Tokens += figures.Tokens;
            stageSeconds[p.Stage] = Math.Max(stageSeconds.TryGetValue(p.Stage, out var s) ? s : 0, figures.Seconds);
        }

        // The main chat: per part a step to hand it out and one to check what
        // came back, one per stage to read the results, one for the summary.
        var stages = stageSeconds.Count;
        var orchestrator = Run(main, context, parts.Count * OrchestratorTokensPerPart, 2 * parts.Count + stages + 1, startCached: true);
        agents.CostUsd += orchestrator.CostUsd;
        agents.Tokens += orchestrator.Tokens;
        agents.Seconds = orchestrator.Seconds + stageSeconds.Values.Sum();
        estimate.Agents = agents;

        // Earlier runs: the measured figures against what was estimated. The
        // same correction goes to "alone", which no one measures, so the two
        // stay comparable.
        if (calibration is { Runs: > 0 })
        {
            estimate.CalibrationRuns = calibration.Runs;
            estimate.CostFactor = calibration.CostFactor;
            estimate.TimeFactor = calibration.TimeFactor;
            Scale(estimate.Alone, calibration.CostFactor, calibration.TimeFactor);
            Scale(estimate.Agents, calibration.CostFactor, calibration.TimeFactor);
            foreach (var part in estimate.Parts) Scale(part, calibration.CostFactor, calibration.TimeFactor);
        }
        return estimate;
    }

    /// <summary>
    /// <paramref name="steps"/> model calls that start at <paramref name="startContext"/>
    /// tokens and handle <paramref name="work"/> new tokens between them.
    /// </summary>
    /// <param name="startCached">The start is in the cache already (the main chat); an agent writes its own once.</param>
    private static SplitFigures Run(ModelPrice price, long startContext, long work, int steps, bool startCached)
    {
        steps = Math.Max(1, steps);
        var output = (long)(work * OutputShare);
        var written = work - output + (startCached ? 0 : startContext);
        // Each step reads the cache: the start, and on average half the new work.
        var read = steps * (startContext + work / 2);
        var cost = (read * price.CacheRead + written * price.CacheWrite + output * price.Output) / 1_000_000m;
        return new SplitFigures
        {
            CostUsd = cost,
            Tokens = read + written + output,
            Seconds = output / (double)Math.Max(1, price.OutputTokensPerSecond) + steps * SecondsPerStep,
        };
    }

    // Tokens follow the cost correction: the turn's tokens are measured too,
    // and what makes the cost off (more steps, longer reports) makes them off.
    private static void Scale(SplitFigures f, double cost, double time)
    {
        f.CostUsd *= (decimal)cost;
        f.Tokens = (long)(f.Tokens * cost);
        f.Seconds *= time;
    }

    // ── Text for the card and the summary ────────────────────────────────

    public static string Money(decimal usd) =>
        "$" + (usd < 0.01m && usd > 0 ? "0.01" : usd.ToString("0.00", CultureInfo.InvariantCulture));

    public static string Duration(double seconds)
    {
        var total = (int)Math.Round(seconds);
        if (total < 60) return Math.Max(1, total) + " s";
        var m = total / 60;
        var s = total % 60;
        return m < 10 && s != 0 ? $"{m} min {s} s" : $"{(int)Math.Round(seconds / 60)} min";
    }

    /// <summary>
    /// The notice after a plan ran: estimate, measured and the estimate for
    /// working alone, side by side. Markdown, shown in the chat.
    /// </summary>
    /// <param name="parts">How many parts the plan had.</param>
    /// <param name="agentTokens">Tokens of the subagents, from their steps; 0 when not known.</param>
    /// <param name="turnTokens">Tokens of the whole turn, main chat included.</param>
    /// <param name="turnCost">What the turn cost, as the CLI reported; null when it did not.</param>
    public static string Summary(int parts, long agentTokens, long turnTokens, decimal? turnCost, double seconds, SplitEstimate estimate)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"**Done with {parts} {(parts == 1 ? "agent" : "agents")}.**");
        if (agentTokens > 0)
            sb.Append($" The agents used {Tokens(agentTokens)} of the turn's {Tokens(turnTokens)} tokens; the rest is the main chat handing out the parts and reading the results.");
        sb.AppendLine().AppendLine();
        sb.AppendLine("| | Estimate | Actual | Alone (est.) |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| Time | {Duration(estimate.Agents.Seconds)} | {(seconds > 0 ? Duration(seconds) : "—")} | {Duration(estimate.Alone.Seconds)} |");
        sb.AppendLine($"| Cost | {Money(estimate.Agents.CostUsd)} | {(turnCost is decimal c && c > 0 ? Money(c) : "—")} | {Money(estimate.Alone.CostUsd)} |");
        sb.AppendLine($"| Tokens | {Tokens(estimate.Agents.Tokens)} | {Tokens(turnTokens)} | {Tokens(estimate.Alone.Tokens)} |");
        sb.AppendLine();

        string verdict;
        if (turnCost is decimal paid && paid > 0 && estimate.Alone.CostUsd > 0)
        {
            var saving = 1 - (double)(paid / estimate.Alone.CostUsd);
            verdict = saving >= 0.05
                ? $"About {Math.Round(saving * 100):0} % cheaper than the estimate for working alone."
                : saving <= -0.05
                    ? $"About {Math.Round(-saving * 100):0} % more than the estimate for working alone."
                    : "About the same as the estimate for working alone.";
            if (turnTokens > estimate.Alone.Tokens && saving > 0)
                verdict += " More tokens in total, but most of them on cheaper models.";
        }
        else if (turnCost is decimal known && known > 0)
        {
            verdict = "The price list has no price for the main chat's model, so there is nothing to compare with.";
        }
        else
        {
            verdict = "Claude Code did not report the cost of this turn.";
        }
        sb.Append("_").Append(verdict).Append(" \"Alone\" is never measured, only estimated. Each agent's model and tokens are in its step above.");
        if (turnCost is decimal recorded && recorded > 0 && seconds > 0) sb.Append(" This run corrects the next estimate.");
        sb.Append("_");
        return sb.ToString();
    }

    public static string Tokens(long tokens) =>
        tokens >= 999_500 ? (tokens / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : tokens >= 1000 ? (tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One plan that ran: the estimate next to what was measured.</summary>
public sealed class SplitOutcome
{
    public decimal EstimatedCost { get; set; }
    public decimal ActualCost { get; set; }
    public decimal EstimatedAloneCost { get; set; }
    public double EstimatedSeconds { get; set; }
    public double ActualSeconds { get; set; }
    public double EstimatedAloneSeconds { get; set; }

    internal string Serialize() => string.Join("/", new[]
    {
        EstimatedCost.ToString("0.########", CultureInfo.InvariantCulture),
        ActualCost.ToString("0.########", CultureInfo.InvariantCulture),
        EstimatedAloneCost.ToString("0.########", CultureInfo.InvariantCulture),
        EstimatedSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        ActualSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        EstimatedAloneSeconds.ToString("0.###", CultureInfo.InvariantCulture),
    });

    internal static SplitOutcome? Parse(string text)
    {
        var p = text.Split('/');
        if (p.Length != 6) return null;
        decimal D(string s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
        double S(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
        var o = new SplitOutcome
        {
            EstimatedCost = D(p[0]), ActualCost = D(p[1]), EstimatedAloneCost = D(p[2]),
            EstimatedSeconds = S(p[3]), ActualSeconds = S(p[4]), EstimatedAloneSeconds = S(p[5]),
        };
        return o.EstimatedCost > 0 && o.ActualCost >= 0 && o.EstimatedAloneCost > 0
               && o.EstimatedSeconds > 0 && o.ActualSeconds > 0 && o.EstimatedAloneSeconds > 0 ? o : null;
    }
}

/// <summary>
/// The last runs of split plans, which correct the next estimate and tell the
/// suggestion (E65) whether splitting has paid off for this user so far.
/// </summary>
public sealed class SplitCalibration
{
    public const int Keep = 10;

    private readonly List<SplitOutcome> _outcomes;

    public SplitCalibration(IEnumerable<SplitOutcome> outcomes) =>
        _outcomes = outcomes.Where(o => o is not null).Reverse().Take(Keep).Reverse().ToList();

    public static readonly SplitCalibration Empty = new SplitCalibration(Array.Empty<SplitOutcome>());

    public IReadOnlyList<SplitOutcome> Outcomes => _outcomes;
    public int Runs => _outcomes.Count;

    // Medians, so one odd run does not throw the next estimate off; kept
    // within a range where an estimate still means something.
    public double CostFactor => Clamp(Median(_outcomes.Select(o => (double)(o.ActualCost / o.EstimatedCost))));
    public double TimeFactor => Clamp(Median(_outcomes.Select(o => o.ActualSeconds / o.EstimatedSeconds)));

    /// <summary>
    /// What splitting saved against working alone, as a share of the alone
    /// estimate (0.3 = 30 % cheaper); negative when it cost more. Null before
    /// the second run: one run says little.
    ///
    /// "Alone" is never measured, so each run's measured cost is set against
    /// its raw alone estimate with today's correction. The correction is the
    /// median of all runs, so a run that came out cheaper than the others
    /// shows a larger saving, and a dearer one a smaller one: the spread is
    /// measured, while the level still rests on the estimate.
    /// </summary>
    public double? MeasuredSaving
    {
        get
        {
            if (Runs < 2) return null;
            var factor = CostFactor;
            return Median(_outcomes.Select(o => 1 - (double)o.ActualCost / ((double)o.EstimatedAloneCost * factor)));
        }
    }

    public SplitCalibration With(SplitOutcome outcome) => new SplitCalibration(_outcomes.Concat(new[] { outcome }));

    public string Serialize() => string.Join(";", _outcomes.Select(o => o.Serialize()));

    public static SplitCalibration Parse(string? stored) =>
        new SplitCalibration((stored ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(SplitOutcome.Parse).Where(o => o is not null)!);

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 1;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    private static double Clamp(double factor) => double.IsNaN(factor) ? 1 : Math.Max(0.33, Math.Min(3, factor));
}
