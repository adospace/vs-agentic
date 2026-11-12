using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Agents;

/// <summary>
/// The turns kept to check the price list against what the agent charged:
/// the last <see cref="ModelPriceCheck.KeepPerFamily"/> per model family.
/// Stored on one line: "claude-sonnet-5-5/list/20261112/2/4/16829/9852/0/0.10347;…".
/// </summary>
public sealed class PriceSamples
{
    private readonly List<ModelCostSample> _samples;

    private PriceSamples(List<ModelCostSample> samples) => _samples = samples;

    public static readonly PriceSamples Empty = new PriceSamples(new List<ModelCostSample>());

    public IReadOnlyList<ModelCostSample> All => _samples;

    /// <summary>Oldest first.</summary>
    public IEnumerable<ModelCostSample> For(string family) =>
        _samples.Where(s => ModelPriceList.FamilyOf(s.Model) == family);

    /// <summary>This list with the usable samples added; older ones beyond the limit go.</summary>
    public PriceSamples With(IEnumerable<ModelCostSample> samples)
    {
        var all = _samples.Concat(samples.Where(ModelPriceCheck.Usable)).ToList();
        var kept = new List<ModelCostSample>();
        foreach (var family in ModelPriceList.Families)
            kept.AddRange(all.Where(s => ModelPriceList.FamilyOf(s.Model) == family)
                .Skip(Math.Max(0, all.Count(s => ModelPriceList.FamilyOf(s.Model) == family) - ModelPriceCheck.KeepPerFamily)));
        return new PriceSamples(kept.OrderBy(s => s.At).ToList());
    }

    public string Serialize() =>
        string.Join(";", _samples.Select(s => string.Join("/", new[]
        {
            s.Model,
            s.CostBasis,
            s.At.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture),
            N(s.InputTokens), N(s.OutputTokens), N(s.CacheReadTokens), N(s.CacheWrite1hTokens), N(s.CacheWrite5mTokens),
            s.CostUsd.ToString(CultureInfo.InvariantCulture),
        })));

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A damaged entry is skipped; the check then has a turn less.</summary>
    public static PriceSamples Parse(string? stored)
    {
        var list = new List<ModelCostSample>();
        foreach (var entry in (stored ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split('/');
            if (p.Length != 9) continue;
            if (!DateTime.TryParseExact(p[2], "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            var n = new long[5];
            var ok = true;
            for (var i = 0; i < 5; i++)
                ok &= long.TryParse(p[3 + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]) && n[i] >= 0;
            if (!ok || !decimal.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var cost)) continue;
            var sample = new ModelCostSample
            {
                Model = p[0], CostBasis = p[1], At = at,
                InputTokens = n[0], OutputTokens = n[1], CacheReadTokens = n[2], CacheWrite1hTokens = n[3], CacheWrite5mTokens = n[4],
                CostUsd = cost, LargestPrompt = 0,
            };
            if (ModelPriceCheck.Usable(sample)) list.Add(sample);
        }
        return new PriceSamples(list.OrderBy(s => s.At).ToList());
    }
}

/// <summary>What the agent charged for one model family, against the price list.</summary>
public sealed class PriceMismatch
{
    public string Family { get; set; } = "";

    /// <summary>The model id of the latest turn, e.g. claude-sonnet-5-5.</summary>
    public string Model { get; set; } = "";

    /// <summary>Charged ÷ price list, over the last turns.</summary>
    public double Ratio { get; set; }

    public int Turns { get; set; }
    public DateTime LastTurn { get; set; }

    /// <summary>The organization set the prices (costBasis "managed").</summary>
    public bool Managed { get; set; }

    /// <summary>The prices that give what the agent charged.</summary>
    public ModelPrice Measured { get; set; } = null!;

    /// <summary>
    /// True when the turns gave each price on its own; false when they only
    /// gave the ratio, and <see cref="Measured"/> is the list times it.
    /// </summary>
    public bool EachPriceMeasured { get; set; }
}

/// <summary>What the Prices page and the strip in the chat say about a difference.</summary>
public static class PriceText
{
    /// <summary>"Claude Code charged Sonnet 1.5 × this list (claude-sonnet-5-5, 6 replies, last on 12. 11. 2026)."</summary>
    public static string Describe(PriceMismatch m)
    {
        var text = string.Format(CultureInfo.InvariantCulture,
            "Claude Code charged {0} {1} this list ({2}, {3} {4}, last on {5}).",
            Family(m.Family), ModelPriceCheck.FormatRatio(m.Ratio), m.Model, m.Turns, m.Turns == 1 ? "reply" : "replies",
            m.LastTurn.ToString("d. M. yyyy", CultureInfo.InvariantCulture));
        if (m.Managed) text += " These are your organization's prices.";
        if (!m.EachPriceMeasured) text += " These replies do not tell the prices apart yet; a few more replies will.";
        return text;
    }

    /// <summary>The line in the chat.</summary>
    public static string Headline(PriceMismatch m) =>
        $"Claude Code charged {Family(m.Family)} {ModelPriceCheck.FormatRatio(m.Ratio)} the prices the split estimate uses.";

    /// <summary>The line under it: the model, the turns, and what would change.</summary>
    public static string Detail(PriceMismatch m, ModelPrice listed)
    {
        var text = string.Format(CultureInfo.InvariantCulture, "{0}, last {1} {2}{3}.",
            m.Model, m.Turns, m.Turns == 1 ? "reply" : "replies", m.Managed ? ", your organization's prices" : "");
        var change = ModelPriceCheck.DescribeChange(listed, m.Measured);
        if (change.Length > 0) text += " " + change + " (US dollars per million tokens).";
        return text;
    }

    /// <summary>The source line for prices taken from Claude Code.</summary>
    public static string MeasuredSource(IEnumerable<PriceMismatch> mismatches, DateTime when) =>
        "Measured from what Claude Code charged, " + when.ToString("d. M. yyyy", CultureInfo.InvariantCulture) + ": "
        + string.Join(", ", mismatches.Select(m => m.Model + (m.Managed ? " (organization prices)" : "")))
        + ".";

    /// <summary>
    /// The source after some families took Claude Code's prices: what the
    /// others came from stays, unless it was an earlier measurement.
    /// </summary>
    public static string SourceAfter(string oldSource, IEnumerable<PriceMismatch> mismatches, DateTime when)
    {
        var measured = MeasuredSource(mismatches, when);
        var old = (oldSource ?? "").Trim();
        if (old.Length == 0 || old.StartsWith("Measured from what Claude Code charged", StringComparison.Ordinal))
            return measured;
        return measured + " Other models: " + (old == ModelPriceList.DefaultSource ? "built-in prices." : old);
    }

    public static string Family(string family) =>
        family.Length == 0 ? family : char.ToUpperInvariant(family[0]) + family.Substring(1);
}

/// <summary>
/// Compares the price list with what the agent charged (E67). The agent's
/// cost is exact: CLI 2.1.319 reports it per model with the tokens by kind,
/// and the built-in list reproduces it to the last digit. A difference means
/// the list is out of date or the organization has its own prices.
/// </summary>
public static class ModelPriceCheck
{
    /// <summary>A difference below this is rounding, not a different price.</summary>
    public const double Tolerance = 0.03;

    /// <summary>
    /// A kept difference asks again only beyond this. The ratio is a blend
    /// over the last turns and moves with their mix of tokens when only some
    /// prices differ.
    /// </summary>
    public const double KeptTolerance = 0.10;

    public const int KeepPerFamily = 12;

    /// <summary>The ratio is taken over this many of the latest turns.</summary>
    public const int Compared = 6;

    /// <summary>
    /// Haiku 5.5 costs more above a prompt of 100K tokens ($0.50 / $2.50); the
    /// list holds the lower price, so turns with a larger prompt are left out.
    /// </summary>
    public static long? PromptLimit(string family) => family == "haiku" ? 100_000 : (long?)null;

    /// <summary>A turn the list can be compared with.</summary>
    public static bool Usable(ModelCostSample s)
    {
        if (s.CostUsd <= 0) return false;
        if (ModelPriceList.FamilyOf(s.Model) is not { } family) return false;
        if (s.CostBasis is not ("list" or "managed")) return false;
        if (s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheWrite1hTokens + s.CacheWrite5mTokens <= 0) return false;
        // Stored samples (0) were checked when they came in.
        return PromptLimit(family) is not long limit || (s.LargestPrompt >= 0 && s.LargestPrompt <= limit);
    }

    /// <summary>What the list gives for the turn's tokens, in dollars.</summary>
    public static decimal ListCost(ModelPrice p, ModelCostSample s) =>
        ((s.InputTokens + 1.25m * s.CacheWrite5mTokens) * p.Input
         + s.OutputTokens * p.Output
         + s.CacheReadTokens * p.CacheRead
         + s.CacheWrite1hTokens * p.CacheWrite) / 1_000_000m;

    /// <summary>
    /// The difference for one family, or null when there are no turns or the
    /// list gives what the agent charged. Only turns of the latest model count:
    /// an older model of the family can have other prices.
    /// </summary>
    public static PriceMismatch? Check(ModelPriceList list, PriceSamples samples, string family)
    {
        var all = samples.For(family).ToList();
        if (all.Count == 0) return null;
        var model = all[all.Count - 1].Model;
        var turns = all.Where(s => string.Equals(s.Model, model, StringComparison.OrdinalIgnoreCase)).ToList();
        var price = list.For(family);

        var recent = turns.Skip(Math.Max(0, turns.Count - Compared)).ToList();
        var listed = recent.Sum(s => ListCost(price, s));
        if (listed <= 0) return null;
        var ratio = (double)(recent.Sum(s => s.CostUsd) / listed);
        if (Math.Abs(ratio - 1) <= Tolerance) return null;

        var measured = Fit(recent, price, ratio, out var each);
        return new PriceMismatch
        {
            Family = family,
            Model = model,
            Ratio = ratio,
            Turns = recent.Count,
            LastTurn = recent[recent.Count - 1].At,
            Managed = recent[recent.Count - 1].CostBasis == "managed",
            Measured = measured,
            EachPriceMeasured = each,
        };
    }

    /// <summary>The largest difference over all families, or null.</summary>
    public static PriceMismatch? Largest(ModelPriceList list, PriceSamples samples) =>
        ModelPriceList.Families.Select(f => Check(list, samples, f))
            .Where(m => m is not null)
            .OrderByDescending(m => Math.Abs(Math.Log(m!.Ratio)))
            .FirstOrDefault();

    /// <summary>The list with one family's prices replaced by the measured ones.</summary>
    public static ModelPriceList WithMeasured(ModelPriceList list, PriceMismatch mismatch, string source) =>
        new ModelPriceList(list.All.Select(p => p.Family == mismatch.Family ? mismatch.Measured : p), source);

    /// <summary>
    /// The prices that give the agent's cost for the turns. Each price is
    /// solved for (least squares, each turn weighted equally) where the turns
    /// tell it apart: a turn that only reads the cache gives the read price, a
    /// turn that writes it the write price. A kind of token that hardly costs
    /// anything in these turns cannot be measured: it follows the measured
    /// prices when they all changed by the same factor (a discount, say) and
    /// stays as it is when only some changed. When the turns do not tell the
    /// prices apart, every price is scaled by the ratio.
    /// </summary>
    internal static ModelPrice Fit(IReadOnlyList<ModelCostSample> turns, ModelPrice current, double ratio, out bool eachMeasured)
    {
        eachMeasured = false;
        var prices = new[] { (double)current.Input, (double)current.Output, (double)current.CacheRead, (double)current.CacheWrite };
        var scaled = prices.Select(p => p * ratio).ToArray();

        var rows = turns.Select(s => (X: new[]
        {
            s.InputTokens + 1.25 * s.CacheWrite5mTokens, (double)s.OutputTokens, (double)s.CacheReadTokens, (double)s.CacheWrite1hTokens,
        }, Y: (double)s.CostUsd * 1_000_000)).ToList();

        var total = rows.Sum(r => Enumerable.Range(0, 4).Sum(j => r.X[j] * prices[j]));
        var active = Enumerable.Range(0, 4)
            .Where(j => prices[j] > 0 && total > 0 && rows.Sum(r => r.X[j] * prices[j]) / total >= 0.02)
            .ToArray();

        var solved = Solve(rows, prices, scaled, active, ratio);
        if (solved is not null && active.Length < 4)
        {
            var factors = active.Select(j => solved[j] / prices[j]).ToArray();
            if (factors.Max() / factors.Min() - 1 > Tolerance)
            {
                // Only some prices changed: the ones not measured keep theirs.
                var again = Solve(rows, prices, prices, active, ratio);
                if (again is not null) solved = again;
            }
        }
        if (solved is not null)
        {
            eachMeasured = true;
            scaled = solved;
        }
        return new ModelPrice(current.Family, Round(scaled[0]), Round(scaled[1]), Round(scaled[2]), Round(scaled[3]), current.OutputTokensPerSecond);
    }

    // Unknowns are multipliers of the current prices, so they are all near 1
    // and the equations stay well scaled. Null when the turns do not settle
    // them or the answer does not reproduce every turn to 1 %.
    private static double[]? Solve(List<(double[] X, double Y)> rows, double[] prices, double[] scaled, int[] active, double ratio)
    {
        var k = active.Length;
        // One turn more than prices: with as many turns as prices any answer fits exactly.
        if (k == 0 || rows.Count <= k) return null;

        var a = new double[k, k];
        var b = new double[k];
        foreach (var (x, y) in rows)
        {
            if (y <= 0) continue;
            var fixedCost = Enumerable.Range(0, 4).Where(j => !active.Contains(j)).Sum(j => x[j] * scaled[j]);
            var target = (y - fixedCost) / y;
            var row = active.Select(j => x[j] * prices[j] / y).ToArray();
            for (var i = 0; i < k; i++)
            {
                b[i] += row[i] * target;
                for (var j = 0; j < k; j++) a[i, j] += row[i] * row[j];
            }
        }

        var m = Gauss(a, b, k);
        if (m is null) return null;

        var result = (double[])scaled.Clone();
        for (var i = 0; i < k; i++)
        {
            // A price far from both the list and the ratio is an artefact of
            // turns that hardly tell two prices apart.
            if (m[i] < 0.2 * Math.Min(1, ratio) || m[i] > 5 * Math.Max(1, ratio))
                return null;
            result[active[i]] = prices[active[i]] * m[i];
        }

        foreach (var (x, y) in rows)
        {
            if (y <= 0) continue;
            var fit = Enumerable.Range(0, 4).Sum(j => x[j] * result[j]);
            if (Math.Abs(fit - y) / y > 0.01) return null;
        }
        return result;
    }

    private static double[]? Gauss(double[,] a, double[] b, int n)
    {
        var maxDiag = 0.0;
        for (var i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, Math.Abs(a[i, i]));
        if (maxDiag <= 0) return null;

        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
                if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
            // Turns whose token mixes are nearly proportional do not tell the
            // prices apart, though some answer reproduces them.
            if (Math.Abs(a[pivot, c]) < 1e-3 * maxDiag) return null;
            if (pivot != c)
            {
                for (var j = 0; j < n; j++) (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
                (b[c], b[pivot]) = (b[pivot], b[c]);
            }
            for (var r = 0; r < n; r++)
            {
                if (r == c) continue;
                var f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (var j = c; j < n; j++) a[r, j] -= f * a[c, j];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = b[i] / a[i, i];
        return x;
    }

    /// <summary>Four significant digits: 0.0999999 → 0.1, 3.00004 → 3.</summary>
    internal static decimal Round(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return 0m;
        var digits = Math.Max(0, Math.Min(10, 3 - (int)Math.Floor(Math.Log10(value))));
        return (decimal)Math.Round(value, digits);
    }

    // ── "Keep my prices": a difference the user chose to keep ─────────────

    /// <summary>Kept differences, stored as "sonnet=1.5;opus=0.8".</summary>
    public static IReadOnlyDictionary<string, double> ParseKept(string? stored)
    {
        var kept = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (stored ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            if (double.TryParse(entry.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
                kept[entry.Substring(0, eq).Trim()] = r;
        }
        return kept;
    }

    public static string SerializeKept(IReadOnlyDictionary<string, double> kept) =>
        string.Join(";", kept.Select(k => k.Key + "=" + k.Value.ToString("0.####", CultureInfo.InvariantCulture)));

    /// <summary>The user kept a difference of about this size; a new one asks again.</summary>
    public static bool IsKept(PriceMismatch m, IReadOnlyDictionary<string, double> kept) =>
        kept.TryGetValue(m.Family, out var r) && Math.Abs(m.Ratio / r - 1) <= KeptTolerance;

    /// <summary>"1.5 ×", "0.8 ×".</summary>
    public static string FormatRatio(double ratio) =>
        ratio.ToString(ratio >= 10 ? "0" : "0.##", CultureInfo.InvariantCulture) + " ×";

    /// <summary>"Input 2 → 3, output 10 → 15" for the prices that change.</summary>
    public static string DescribeChange(ModelPrice from, ModelPrice to)
    {
        var parts = new List<string>();
        void Add(string name, decimal a, decimal b)
        {
            if (a != b) parts.Add($"{name} {ModelPriceList.Plain(a)} → {ModelPriceList.Plain(b)}");
        }
        Add("input", from.Input, to.Input);
        Add("output", from.Output, to.Output);
        Add("cache read", from.CacheRead, to.CacheRead);
        Add("cache write", from.CacheWrite, to.CacheWrite);
        if (parts.Count == 0) return "";
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
