using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Agents;

namespace VsAgentic.UI.ViewModels;

// ── The split estimate's prices against what Claude Code charged (E67) ─────
// After each turn the agent reports what each model cost. When that differs
// from the price list, a strip above the message box says so and offers to
// take the measured prices. The list changes only on the user's click.
public partial class ChatSessionViewModel
{
    /// <summary>The strip about prices; null when hidden.</summary>
    [ObservableProperty]
    private PriceHintViewModel? _priceHint;

    // Families closed with × in this session: asked again in the next one.
    private readonly HashSet<string> _priceHintClosed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private void SubscribeModelCost(IChatService chatService)
    {
        if (chatService is IModelCostSource source) source.ModelCostMeasured += OnModelCostMeasured;
    }

    private void UnsubscribeModelCost(IChatService? chatService)
    {
        if (chatService is IModelCostSource source) source.ModelCostMeasured -= OnModelCostMeasured;
    }

    // From the agent's reader thread.
    private void OnModelCostMeasured(IReadOnlyList<ModelCostSample> samples) => Dispatch(() =>
    {
        if (SplitSettings is not { } settings) return;
        try
        {
            settings.RecordCosts(samples);
            UpdatePriceHint();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[VM] Could not compare the price list with Claude Code's cost");
        }
    });

    private void UpdatePriceHint()
    {
        // The prices serve the split estimate only; without it there is nothing to correct.
        if (SplitSettings is not { } settings || _splitPlanner is null) return;
        var prices = settings.Prices;
        var samples = settings.PriceSamples;
        var mismatch = ModelPriceList.Families
            .Select(f => ModelPriceCheck.Check(prices, samples, f))
            // Offered only once the replies give each price: one reply gives
            // only the ratio, and scaling every price by it changed input and
            // output too when only the cache prices differed (manual test 12. 11.).
            .Where(m => m is not null && m.EachPriceMeasured && !settings.IsKept(m) && !_priceHintClosed.Contains(m.Family))
            .OrderByDescending(m => Math.Abs(Math.Log(m!.Ratio)))
            .FirstOrDefault();
        // A confirmation stays, with its Undo, until a difference in another
        // model needs the strip.
        if (PriceHint is { IsConfirmation: true } done && (mismatch is null || mismatch.Family == done.Family)) return;
        if (mismatch is null)
        {
            PriceHint = null;
            return;
        }

        var family = mismatch.Family;
        PriceHint = PriceHintViewModel.Suggest(family,
            PriceText.Headline(mismatch),
            PriceText.Detail(mismatch, prices.For(family)),
            accept: () => AcceptMeasuredPrices(family),
            keep: () => KeepListedPrices(family),
            dismiss: () =>
            {
                _priceHintClosed.Add(family);
                PriceHint = null;
            });
    }

    private void AcceptMeasuredPrices(string family)
    {
        if (SplitSettings is not { } settings) return;
        var before = settings.Prices;
        // Checked again: another chat may have taken the prices meanwhile.
        if (ModelPriceCheck.Check(before, settings.PriceSamples, family) is not { EachPriceMeasured: true } mismatch)
        {
            PriceHint = null;
            return;
        }

        var after = ModelPriceCheck.WithMeasured(before, mismatch, PriceText.SourceAfter(before.Source, new[] { mismatch }, DateTime.Now));
        settings.SavePrices(after);
        var change = ModelPriceCheck.DescribeChange(before.For(family), after.For(family));
        PriceHint = PriceHintViewModel.Accepted(family,
            $"{PriceText.Family(family)} prices now match what Claude Code charged.",
            (change.Length > 0 ? change + ". " : "") + "See Tools › Options › VsAgentic › Prices.",
            dismiss: () => PriceHint = null,
            undo: () =>
            {
                // Only this model goes back: the list may have changed since,
                // in another chat or on the Prices page.
                var now = settings.Prices;
                settings.SavePrices(new ModelPriceList(
                    now.All.Select(p => p.Family == family ? before.For(family) : p),
                    before.Source));
                PriceHint = null;
            });
    }

    private void KeepListedPrices(string family)
    {
        if (SplitSettings is { } settings && ModelPriceCheck.Check(settings.Prices, settings.PriceSamples, family) is { } mismatch)
            settings.Keep(mismatch);
        PriceHint = null;
    }
}
