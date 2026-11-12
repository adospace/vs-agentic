using System;
using System.Collections.Generic;
using System.Text.Json;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Turns the result's <c>modelUsage</c> into the cost of one turn per model.
///
/// Measured on CLI 2.1.319: <c>modelUsage</c> is the conversation's running
/// total per model, not the turn's, and a process started with
/// <c>--resume</c> carries the earlier turns in it. So a turn is the
/// difference from the last result of the same conversation. The first
/// result this host sees for a resumed conversation is a total over turns it did not
/// watch; it is still a valid measurement (prices are linear), only the size
/// of its prompts is not known.
/// </summary>
internal sealed class ModelCostReader
{
    private readonly object _lock = new object();
    private string? _sessionId;
    private readonly Dictionary<string, Totals> _previous = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _largestPrompt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    private sealed class Totals
    {
        public long Input, Output, Read, Write1h, Write5m, WebSearches, WriteKindUnknown;
        public decimal Cost;
    }

    /// <summary>One request's prompt, from an assistant event (main conversation or subagent).</summary>
    public void NotePrompt(string? model, long promptTokens)
    {
        if (string.IsNullOrEmpty(model) || promptTokens <= 0) return;
        lock (_lock)
        {
            var key = BaseModel(model!);
            if (!_largestPrompt.TryGetValue(key, out var max) || promptTokens > max)
                _largestPrompt[key] = promptTokens;
        }
    }

    /// <summary>
    /// A conversation the host started itself, not resumed: its totals start
    /// at 0, so its first turn is a turn like any other.
    /// </summary>
    public void Begin(string sessionId)
    {
        lock (_lock)
        {
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) return;
            _sessionId = sessionId;
            _previous.Clear();
        }
    }

    /// <summary>A new conversation: nothing before it counts.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _sessionId = null;
            _previous.Clear();
            _largestPrompt.Clear();
        }
    }

    /// <summary>The turn's cost per model, or an empty list when it cannot be compared with a price list.</summary>
    public IReadOnlyList<ModelCostSample> Read(JsonElement result)
    {
        var samples = new List<ModelCostSample>();
        lock (_lock)
        {
            var largest = new Dictionary<string, long>(_largestPrompt, StringComparer.OrdinalIgnoreCase);
            _largestPrompt.Clear();

            if (!result.TryGetProperty("modelUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return samples;

            // Without an id, one conversation is assumed: otherwise each turn
            // would report the whole conversation again.
            var sessionId = CliProcessInfo.ReadString(result, "session_id") ?? "";
            var known = string.Equals(sessionId, _sessionId, StringComparison.Ordinal);
            if (!known)
            {
                _previous.Clear();
                _sessionId = sessionId;
            }

            // Fast mode costs more for the same tokens (Opus 5.5: 2 ×); the
            // result says which speed ran.
            var fast = result.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                && CliProcessInfo.ReadString(u, "speed") is { } speed && !string.Equals(speed, "standard", StringComparison.OrdinalIgnoreCase);

            // "claude-opus-5" and "claude-opus-5[1m]" are one model at one
            // price; their totals are added before the difference is taken.
            var byModel = new Dictionary<string, (Totals Totals, string Basis)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in usage.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var key = BaseModel(entry.Name);
                var totals = ReadTotals(entry.Value);
                var basis = CliProcessInfo.ReadString(entry.Value, "costBasis") ?? "";
                byModel[key] = byModel.TryGetValue(key, out var had) ? (Plus(had.Totals, totals), basis.Length > 0 ? basis : had.Basis) : (totals, basis);
            }

            foreach (var pair in byModel)
            {
                var key = pair.Key;
                var now = pair.Value.Totals;
                var basis = pair.Value.Basis;
                _previous.TryGetValue(key, out var before);
                _previous[key] = now;

                // A model new to a followed conversation started this turn. A
                // smaller total than before is a conversation this reader did
                // not follow; then the total is all it has.
                var counted = before is null ? known : !Less(now, before);
                var turn = before is not null && counted ? Minus(now, before) : now;

                if (fast || turn.Cost <= 0 || turn.WebSearches > 0) continue;
                // Cache writes without the split into 5-minute and 1-hour cost
                // 1.25 × or 2 × input; such a turn cannot be priced.
                if (turn.WriteKindUnknown > 0) continue;
                if (basis.Length > 0 && !string.Equals(basis, "list", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(basis, "managed", StringComparison.OrdinalIgnoreCase))
                    continue;

                samples.Add(new ModelCostSample
                {
                    Model = key,
                    InputTokens = turn.Input,
                    OutputTokens = turn.Output,
                    CacheReadTokens = turn.Read,
                    CacheWrite1hTokens = turn.Write1h,
                    CacheWrite5mTokens = turn.Write5m,
                    CostUsd = turn.Cost,
                    CostBasis = basis.Length > 0 ? basis.ToLowerInvariant() : "list",
                    // Over turns this reader did not see, the prompts are unknown.
                    LargestPrompt = known && counted && largest.TryGetValue(key, out var p) ? p : -1,
                    At = DateTime.Now,
                });
            }
        }
        return samples;
    }

    private static Totals ReadTotals(JsonElement e)
    {
        var write = Count(e, "cacheCreationInputTokens");
        var has5m = e.TryGetProperty("cacheCreationEphemeral5mInputTokens", out _);
        var has1h = e.TryGetProperty("cacheCreationEphemeral1hInputTokens", out _);
        var write5m = Count(e, "cacheCreationEphemeral5mInputTokens");
        var write1h = has1h ? Count(e, "cacheCreationEphemeral1hInputTokens") : has5m ? Math.Max(0, write - write5m) : 0;
        return new Totals
        {
            WriteKindUnknown = has5m || has1h ? 0 : write,
            Input = Count(e, "inputTokens"),
            Output = Count(e, "outputTokens"),
            Read = Count(e, "cacheReadInputTokens"),
            Write1h = write1h,
            Write5m = write5m,
            WebSearches = Count(e, "webSearchRequests"),
            Cost = e.TryGetProperty("costUSD", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var d) ? d : 0m,
        };
    }

    private static bool Less(Totals a, Totals b) =>
        a.Input < b.Input || a.Output < b.Output || a.Read < b.Read || a.Write1h < b.Write1h
        || a.Write5m < b.Write5m || a.Cost < b.Cost;

    private static Totals Plus(Totals a, Totals b) => new Totals
    {
        Input = a.Input + b.Input,
        Output = a.Output + b.Output,
        Read = a.Read + b.Read,
        Write1h = a.Write1h + b.Write1h,
        Write5m = a.Write5m + b.Write5m,
        WebSearches = a.WebSearches + b.WebSearches,
        WriteKindUnknown = a.WriteKindUnknown + b.WriteKindUnknown,
        Cost = a.Cost + b.Cost,
    };

    private static Totals Minus(Totals a, Totals b) => new Totals
    {
        WriteKindUnknown = a.WriteKindUnknown - b.WriteKindUnknown,
        Input = a.Input - b.Input,
        Output = a.Output - b.Output,
        Read = a.Read - b.Read,
        Write1h = a.Write1h - b.Write1h,
        Write5m = a.Write5m - b.Write5m,
        WebSearches = a.WebSearches - b.WebSearches,
        Cost = a.Cost - b.Cost,
    };

    private static long Count(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) && v > 0 ? v : 0;

    /// <summary>"claude-opus-5[1m]" → "claude-opus-5".</summary>
    private static string BaseModel(string id)
    {
        var bracket = id.IndexOf('[');
        return bracket > 0 ? id.Substring(0, bracket) : id;
    }
}
