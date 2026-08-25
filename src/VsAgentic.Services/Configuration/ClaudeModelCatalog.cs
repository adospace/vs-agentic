using System;
using System.Collections.Generic;
using System.Linq;

namespace VsAgentic.Services.Configuration;

/// <summary>
/// Reasoning effort for a session, mapping 1:1 onto the CLI's <c>--effort</c>
/// levels.
///
/// There is deliberately no "default" member. The CLI does not report the
/// effort it is running with — its <c>system/init</c> event names the model,
/// the permission mode and the output style, but not this — so a "default"
/// selection would be a value the header could never truthfully display. The
/// extension therefore always passes the flag: whatever the dropdown shows is
/// what the next turn will run with, by construction. The cost is that a level
/// configured elsewhere in the user's own CLI settings is overridden.
/// </summary>
public enum ClaudeEffort
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public static class ClaudeEffortExtensions
{
    /// <summary>The spelling <c>--effort</c> expects.</summary>
    public static string ToCliValue(this ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => "low",
        ClaudeEffort.Medium => "medium",
        ClaudeEffort.XHigh => "xhigh",
        ClaudeEffort.Max => "max",
        _ => "high",
    };

    /// <summary>Short label for the header dropdown.</summary>
    public static string ToDisplayName(this ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => "Low",
        ClaudeEffort.Medium => "Medium",
        ClaudeEffort.XHigh => "XHigh",
        ClaudeEffort.Max => "Max",
        _ => "High",
    };
}

/// <summary>
/// One entry in the model dropdown. <see cref="Alias"/> is what goes to
/// <c>--model</c>; an empty alias means "leave the CLI's own choice alone".
/// </summary>
public sealed class ClaudeModelInfo
{
    public ClaudeModelInfo(string alias, string displayName)
    {
        Alias = alias;
        DisplayName = displayName;
    }

    public string Alias { get; }

    public string DisplayName { get; }

    public bool IsCliDefault => Alias.Length == 0;

    public override string ToString() => DisplayName;
}

public static class ClaudeModelCatalog
{
    /// <summary>Context window every current model ships with by default.</summary>
    public const int StandardContextWindowTokens = 200_000;

    /// <summary>
    /// Context window of the long-context variants, whose model ids carry a
    /// <c>[1m]</c> suffix (e.g. <c>claude-opus-5[1m]</c>).
    /// </summary>
    public const int LongContextWindowTokens = 1_000_000;

    /// <summary>
    /// Aliases offered in the header dropdown. These are the aliases the CLI
    /// documents for <c>--model</c>, which always resolve to the latest model
    /// in each family — so this list does not need touching when a new model
    /// ships.
    /// </summary>
    public static IReadOnlyList<ClaudeModelInfo> All { get; } = new[]
    {
        new ClaudeModelInfo("", "Default"),
        new ClaudeModelInfo("opus", "Opus"),
        new ClaudeModelInfo("sonnet", "Sonnet"),
        new ClaudeModelInfo("haiku", "Haiku"),
        new ClaudeModelInfo("fable", "Fable"),
    };

    public static ClaudeModelInfo Default => All[0];

    /// <summary>
    /// Resolves a stored alias back to its catalog entry, falling back to
    /// <see cref="Default"/> so a stale or hand-edited setting cannot leave the
    /// dropdown with nothing selected.
    /// </summary>
    public static ClaudeModelInfo Find(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return Default;
        return All.FirstOrDefault(m => string.Equals(m.Alias, alias, StringComparison.OrdinalIgnoreCase))
            ?? Default;
    }

    /// <summary>
    /// Context window for the concrete model id the CLI reports in its
    /// <c>system/init</c> event. We read it off the id rather than the selected
    /// alias because the alias resolves server-side — the init event is the
    /// only place that names what is actually running.
    /// </summary>
    public static int ContextWindowFor(string? reportedModelId) =>
        !string.IsNullOrEmpty(reportedModelId)
        && reportedModelId!.IndexOf("[1m]", StringComparison.OrdinalIgnoreCase) >= 0
            ? LongContextWindowTokens
            : StandardContextWindowTokens;

    /// <summary>
    /// Turns a reported model id into something short enough for the header
    /// ("claude-opus-5[1m]" → "Opus 5"). Unrecognized ids come back unchanged
    /// so a new family still shows something truthful.
    /// </summary>
    public static string DisplayNameFor(string? reportedModelId)
    {
        if (string.IsNullOrWhiteSpace(reportedModelId)) return Default.DisplayName;

        var id = reportedModelId!;

        // Drop the context-window suffix and any date stamp, then title-case the
        // family: "claude-opus-5[1m]" → "opus-5", "claude-haiku-4-5-20251001" → "haiku-4-5".
        var bracket = id.IndexOf('[');
        if (bracket > 0) id = id.Substring(0, bracket);
        if (id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            id = id.Substring("claude-".Length);

        var parts = id.Split('-')
            .Where(p => p.Length > 0 && !(p.Length == 8 && p.All(char.IsDigit)))
            .ToArray();
        if (parts.Length == 0) return reportedModelId!;

        var family = parts[0];
        family = char.ToUpperInvariant(family[0]) + family.Substring(1);
        var version = string.Join(".", parts.Skip(1));

        return version.Length == 0 ? family : family + " " + version;
    }
}
