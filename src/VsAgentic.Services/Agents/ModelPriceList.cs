using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VsAgentic.Services.Agents;

/// <summary>
/// What one model family costs, in US dollars per million tokens, and how
/// fast it writes. The speed is only for the time estimate of a split plan.
/// </summary>
public sealed class ModelPrice
{
    public ModelPrice(string family, decimal input, decimal output, decimal cacheRead, decimal cacheWrite, int outputTokensPerSecond)
    {
        Family = family;
        Input = input;
        Output = output;
        CacheRead = cacheRead;
        CacheWrite = cacheWrite;
        OutputTokensPerSecond = outputTokensPerSecond;
    }

    /// <summary>haiku, sonnet, opus or fable.</summary>
    public string Family { get; }
    public decimal Input { get; }
    public decimal Output { get; }
    public decimal CacheRead { get; }

    /// <summary>A write to the 5-minute cache.</summary>
    public decimal CacheWrite { get; }

    public int OutputTokensPerSecond { get; }
}

/// <summary>
/// The price list the split estimate uses. The user keeps it in
/// Tools › Options › VsAgentic › Prices; the defaults here only fill an empty
/// list, so a price change needs no new release.
/// </summary>
public sealed class ModelPriceList
{
    public static readonly IReadOnlyList<string> Families = new[] { "haiku", "sonnet", "opus", "fable" };

    /// <summary>Where the default prices come from.</summary>
    public const string DefaultSource = "Anthropic API list prices, 12. 11. 2026 (Haiku up to 100K input tokens). Speeds are rough.";

    // Cache writes are 1.25 × input (5-minute cache).
    public static readonly ModelPriceList Defaults = new ModelPriceList(new[]
    {
        new ModelPrice("haiku", 0.10m, 0.50m, 0.01m, 0.125m, 180),
        new ModelPrice("sonnet", 2m, 10m, 0.20m, 2.5m, 90),
        new ModelPrice("opus", 4m, 20m, 0.20m, 5m, 60),
        new ModelPrice("fable", 10m, 50m, 0.25m, 12.5m, 45),
    }, DefaultSource);

    private readonly Dictionary<string, ModelPrice> _byFamily;

    public ModelPriceList(IEnumerable<ModelPrice> prices, string source)
    {
        _byFamily = prices.ToDictionary(p => p.Family, StringComparer.OrdinalIgnoreCase);
        Source = source ?? "";
    }

    public string Source { get; }

    public IEnumerable<ModelPrice> All => Families.Select(f => _byFamily.TryGetValue(f, out var p) ? p : Defaults._byFamily[f]);

    /// <summary>The family of a model alias, id or display name: "claude-sonnet-5-5" → sonnet.</summary>
    public static string? FamilyOf(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        foreach (var f in Families)
            if (m.Contains(f)) return f;
        return null;
    }

    /// <summary>The price of a model; an unknown model is priced as Opus, the session default.</summary>
    public ModelPrice For(string? model)
    {
        var family = FamilyOf(model) ?? "opus";
        return _byFamily.TryGetValue(family, out var p) ? p : Defaults._byFamily[family];
    }

    // ── Stored as one line per family: "sonnet=2/10/0.2/2.5/90" ───────────

    public string Serialize() =>
        string.Join(";", All.Select(p => string.Join("/", new[]
        {
            p.Input.ToString(CultureInfo.InvariantCulture),
            p.Output.ToString(CultureInfo.InvariantCulture),
            p.CacheRead.ToString(CultureInfo.InvariantCulture),
            p.CacheWrite.ToString(CultureInfo.InvariantCulture),
            p.OutputTokensPerSecond.ToString(CultureInfo.InvariantCulture),
        }).Insert(0, p.Family + "=")));

    /// <summary>
    /// Reads a stored list. A family that is missing or does not parse keeps
    /// its default, so a damaged setting never leaves a model without a price.
    /// </summary>
    public static ModelPriceList Parse(string? stored, string? source)
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (stored ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var family = entry.Substring(0, eq).Trim().ToLowerInvariant();
            if (!Families.Contains(family)) continue;
            var parts = entry.Substring(eq + 1).Split('/');
            if (parts.Length != 5) continue;
            if (!TryMoney(parts[0], out var input) || !TryMoney(parts[1], out var output)
                || !TryMoney(parts[2], out var read) || !TryMoney(parts[3], out var write)
                || !int.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
                continue;
            prices[family] = new ModelPrice(family, input, output, read, write, speed);
        }
        foreach (var d in Defaults.All)
            if (!prices.ContainsKey(d.Family)) prices[d.Family] = d;
        return new ModelPriceList(prices.Values, string.IsNullOrWhiteSpace(source) ? DefaultSource : source!);
    }

    /// <summary>
    /// A price with a dot for decimals. A comma is refused rather than read
    /// as a thousands separator, which would make "0,25" cost 25 dollars.
    /// </summary>
    public static bool TryMoney(string text, out decimal value)
    {
        var t = (text ?? "").Trim().TrimStart('$').Trim();
        value = 0;
        return t.Length > 0 && t.IndexOf(',') < 0
            && decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
