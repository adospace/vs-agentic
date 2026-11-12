using System;
using System.Collections.Generic;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// What one model cost in one turn, as the agent reports it: the tokens by
/// kind and the dollars it charged for them. The split estimate compares this
/// with its price list.
/// </summary>
public sealed class ModelCostSample
{
    /// <summary>The model id, e.g. claude-sonnet-5-5.</summary>
    public string Model { get; set; } = "";

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }

    /// <summary>Writes to the 1-hour cache.</summary>
    public long CacheWrite1hTokens { get; set; }

    /// <summary>Writes to the 5-minute cache (1.25 × input).</summary>
    public long CacheWrite5mTokens { get; set; }

    public decimal CostUsd { get; set; }

    /// <summary>
    /// Where the agent's prices come from: "list" (its built-in prices),
    /// "managed" (prices the organization set) or "unknown".
    /// </summary>
    public string CostBasis { get; set; } = "";

    /// <summary>
    /// The largest prompt one request of this model sent in the turn, in
    /// tokens; -1 when not known. Some models cost more above a prompt size.
    /// </summary>
    public long LargestPrompt { get; set; } = -1;

    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>
/// An agent that reports, after each turn, what each model it used cost.
/// Kept apart from <see cref="IChatService"/>: not every agent reports it.
/// </summary>
public interface IModelCostSource
{
    /// <summary>
    /// Raised after a turn, from the agent's reader thread, with one sample
    /// per model the turn used. Turns the price list cannot be compared with
    /// (fast mode, web searches) are not reported.
    /// </summary>
    event Action<IReadOnlyList<ModelCostSample>>? ModelCostMeasured;
}
