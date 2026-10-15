using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VsAgentic.Services.Configuration;

/// <summary>
/// Reasoning effort for a session. Every member except <see cref="Default"/>
/// maps 1:1 onto a level of the CLI's <c>--effort</c>.
///
/// <see cref="Default"/> sends no flag at all, so a CLI older than
/// <c>--effort</c> still starts, and an effort configured in the user's own CLI
/// settings stays in charge. The level the CLI then uses comes from
/// <c>get_settings</c> (<see cref="Abstractions.IChatService.AppliedEffort"/>).
/// </summary>
public enum ClaudeEffort
{
    Default,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public static class ClaudeEffortExtensions
{
    /// <summary>
    /// The spelling <c>--effort</c> expects. Not meaningful for
    /// <see cref="ClaudeEffort.Default"/>, which sends no flag.
    /// </summary>
    public static string ToCliValue(this ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => "low",
        ClaudeEffort.Medium => "medium",
        ClaudeEffort.XHigh => "xhigh",
        ClaudeEffort.Max => "max",
        _ => "high",
    };

    /// <summary>
    /// Reads a level as the CLI spells it. Null for a value this enum does
    /// not know, which a newer CLI may send.
    /// </summary>
    public static ClaudeEffort? FromCliValue(string? value) => value switch
    {
        "low" => ClaudeEffort.Low,
        "medium" => ClaudeEffort.Medium,
        "high" => ClaudeEffort.High,
        "xhigh" => ClaudeEffort.XHigh,
        "max" => ClaudeEffort.Max,
        _ => null,
    };
}

/// <summary>
/// One entry in the model dropdown. <see cref="Alias"/> is what goes to
/// <c>--model</c>; an empty alias means "leave the CLI's own choice alone".
///
/// The entries the CLI reports carry what the model supports. The built-in
/// entries do not: there <see cref="SupportedEfforts"/> is null, which means
/// "not known", and every level is offered.
/// </summary>
public sealed class ClaudeModelInfo
{
    public ClaudeModelInfo(string alias, string displayName)
    {
        Alias = alias;
        DisplayName = displayName;
    }

    public ClaudeModelInfo(
        string alias,
        string displayName,
        string? description,
        string? resolvedModel,
        IReadOnlyList<ClaudeEffort>? supportedEfforts,
        bool supportsFastMode = false)
        : this(alias, displayName)
    {
        Description = description;
        ResolvedModel = resolvedModel;
        SupportedEfforts = supportedEfforts;
        SupportsFastMode = supportsFastMode;
    }

    /// <summary>
    /// Whether the CLI offers fast mode for this model. False for built-in
    /// entries, which do not know.
    /// </summary>
    public bool SupportsFastMode { get; }

    public string Alias { get; }

    public string DisplayName { get; }

    /// <summary>The CLI's one-line description, for a tooltip. Null for built-in entries.</summary>
    public string? Description { get; }

    /// <summary>The model id the entry stands for now ("opus" → "claude-opus-5-5").</summary>
    public string? ResolvedModel { get; }

    /// <summary>
    /// The effort levels the model accepts. Empty when it has no effort
    /// setting at all; null when the CLI has not said.
    /// </summary>
    public IReadOnlyList<ClaudeEffort>? SupportedEfforts { get; }

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
    /// Aliases offered in the model dropdown. These are the aliases the CLI
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

    private static IReadOnlyList<ClaudeModelInfo>? s_reported;

    /// <summary>
    /// The models the CLI offered in its last answer to <c>initialize</c>, from
    /// any session. Null until a CLI has answered; the dropdown then shows
    /// <see cref="All"/>.
    ///
    /// Shared rather than per session: the list depends on the account and the
    /// CLI version, not on the conversation, and a session that has not sent
    /// anything yet has no process to ask.
    /// </summary>
    public static IReadOnlyList<ClaudeModelInfo>? Reported => Volatile.Read(ref s_reported);

    /// <summary>Raised on a background thread when <see cref="Reported"/> changes.</summary>
    public static event Action<IReadOnlyList<ClaudeModelInfo>>? ReportedChanged;

    public static void SetReported(IReadOnlyList<ClaudeModelInfo> models)
    {
        if (models.Count == 0) return;

        // Kept, not replaced, when equal: sessions compare the reference.
        var previous = Volatile.Read(ref s_reported);
        if (previous is not null && SameModels(previous, models)) return;

        Volatile.Write(ref s_reported, models);
        ReportedChanged?.Invoke(models);
    }

    // Each process start reports the list again. Only a different list
    // rebuilds the dropdowns.
    private static bool SameModels(IReadOnlyList<ClaudeModelInfo> a, IReadOnlyList<ClaudeModelInfo> b) =>
        a.Count == b.Count
        && a.Zip(b, (x, y) =>
                x.Alias == y.Alias
                && x.DisplayName == y.DisplayName
                && x.Description == y.Description
                && x.ResolvedModel == y.ResolvedModel
                && x.SupportsFastMode == y.SupportsFastMode
                && (x.SupportedEfforts ?? Array.Empty<ClaudeEffort>())
                    .SequenceEqual(y.SupportedEfforts ?? Array.Empty<ClaudeEffort>()))
            .All(same => same);

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
    /// Turns a reported model id into something short enough for the status bar
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
