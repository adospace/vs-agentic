namespace VsAgentic.Services.Anthropic;

/// <summary>
/// Anthropic API token pricing per model (USD per million tokens).
/// Source: https://www.anthropic.com/pricing
/// CacheCreationPerMillion uses the 5-minute TTL write rate.
/// </summary>
public sealed record ModelTokenPricing(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheCreationPerMillion,
    decimal CacheReadPerMillion);

public static class ModelPricing
{
    private static readonly Dictionary<string, ModelTokenPricing> Prices = new()
    {
        // claude-haiku-4-5-20251001 = Claude Haiku 4.5
        [ModelIds.Haiku] = new ModelTokenPricing(
            InputPerMillion:          1.00m,
            OutputPerMillion:         5.00m,
            CacheCreationPerMillion:  1.25m,
            CacheReadPerMillion:      0.10m),

        // claude-sonnet-4-6 = Claude Sonnet 4.6
        [ModelIds.Sonnet] = new ModelTokenPricing(
            InputPerMillion:          3.00m,
            OutputPerMillion:        15.00m,
            CacheCreationPerMillion:  3.75m,
            CacheReadPerMillion:      0.30m),

        // claude-opus-4-6 = Claude Opus 4.6
        [ModelIds.Opus] = new ModelTokenPricing(
            InputPerMillion:          5.00m,
            OutputPerMillion:        25.00m,
            CacheCreationPerMillion:  6.25m,
            CacheReadPerMillion:      0.50m),
    };

    /// <summary>
    /// Returns the pricing for a given model ID, or null if the model is not recognised.
    /// </summary>
    public static ModelTokenPricing? For(string modelId)
        => Prices.TryGetValue(modelId, out var p) ? p : null;

    /// <summary>
    /// Calculates the cost in USD for a set of token counts at the rates for <paramref name="modelId"/>.
    /// Returns null when the model is not in the pricing table.
    /// </summary>
    public static decimal? CalculateCost(
        string modelId,
        int inputTokens,
        int outputTokens,
        int cacheCreationTokens,
        int cacheReadTokens)
    {
        var p = For(modelId);
        if (p is null) return null;

        return (inputTokens         * p.InputPerMillion         / 1_000_000m)
             + (outputTokens        * p.OutputPerMillion        / 1_000_000m)
             + (cacheCreationTokens * p.CacheCreationPerMillion / 1_000_000m)
             + (cacheReadTokens     * p.CacheReadPerMillion     / 1_000_000m);
    }
}
