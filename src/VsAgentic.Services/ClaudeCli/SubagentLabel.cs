using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The header of a subagent's step: what it was asked to do, then what runs it
/// and what it took, as the CLI reports each part —
/// "List directory files · Explore · Haiku 5.5 · 20.4k tokens · 5 s".
/// The agent type comes with the Agent call, the model with the subagent's
/// first message, the totals with the call's result. Parts not reported yet
/// are left out.
/// </summary>
internal sealed class SubagentLabel
{
    private const string Separator = " · ";

    public SubagentLabel(string description, string? agentType)
    {
        Description = description;
        AgentType = Clean(agentType);
    }

    public string Description { get; }
    public string? AgentType { get; private set; }

    /// <summary>The model id the CLI reported, e.g. claude-haiku-5-5.</summary>
    public string? Model { get; private set; }

    public long? Tokens { get; private set; }
    public long? DurationMs { get; private set; }

    /// <summary>
    /// Takes the model from a message of the subagent. Returns true when that
    /// changed the header. Only the first model counts: a later message on a
    /// fallback model must not relabel the step halfway.
    /// </summary>
    public bool NoteModel(string? model)
    {
        if (Model is not null) return false;
        model = Clean(model);
        // The CLI writes "<synthetic>" on messages it makes up itself.
        if (model is null || model.StartsWith("<", StringComparison.Ordinal)) return false;
        Model = model;
        return true;
    }

    /// <summary>
    /// Takes what the Agent call's <c>tool_use_result</c> reports at the end:
    /// <c>agentType</c>, <c>resolvedModel</c>, <c>totalTokens</c>,
    /// <c>totalDurationMs</c> (CLI 2.1.319). A subagent sent to the background
    /// gets its result at once, with no totals; the header keeps what it has.
    /// </summary>
    public void NoteResult(JsonElement toolUseResult)
    {
        if (toolUseResult.ValueKind != JsonValueKind.Object) return;

        AgentType ??= Clean(ReadString(toolUseResult, "agentType"));
        NoteModel(ReadString(toolUseResult, "resolvedModel"));

        var tokens = ReadLong(toolUseResult, "totalTokens");
        if (tokens > 0) Tokens = tokens;
        var duration = ReadLong(toolUseResult, "totalDurationMs");
        if (duration > 0) DurationMs = duration;
    }

    public override string ToString()
    {
        var sb = new StringBuilder(Description);
        if (AgentType is not null) sb.Append(Separator).Append(AgentType);
        if (Model is not null) sb.Append(Separator).Append(ClaudeModelCatalog.DisplayNameFor(Model));
        if (Tokens is not null) sb.Append(Separator).Append(FormatTokens(Tokens.Value)).Append(" tokens");
        if (DurationMs is not null) sb.Append(Separator).Append(FormatDuration(DurationMs.Value));
        return sb.ToString();
    }

    internal static string FormatTokens(long tokens) =>
        tokens >= 1000
            ? (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : tokens.ToString(CultureInfo.InvariantCulture);

    internal static string FormatDuration(long ms)
    {
        var seconds = (long)Math.Round(ms / 1000.0);
        if (seconds < 1) return "<1 s";
        if (seconds < 60) return seconds.ToString(CultureInfo.InvariantCulture) + " s";
        var minutes = seconds / 60;
        var rest = seconds % 60;
        return rest == 0
            ? $"{minutes} min"
            : $"{minutes} min {rest} s";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long? ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;
}
