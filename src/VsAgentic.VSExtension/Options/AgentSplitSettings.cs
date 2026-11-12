using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Agents;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// The split-suggestion choices, kept on the options page so the user can
/// see and undo them in Tools › Options › VsAgentic › Agents, and the price
/// list and earlier runs the split estimate uses.
/// </summary>
internal sealed class AgentSplitSettings : IAgentSplitSettings
{
    private readonly VsAgenticOptionsPage _page;

    public AgentSplitSettings(VsAgenticOptionsPage page) => _page = page;

    public bool SuggestionsEnabled => _page.SuggestAgentSplit;

    // Read each time: the user may change the prices while a chat is open.
    public ModelPriceList Prices => ModelPriceList.Parse(_page.ModelPrices, _page.ModelPricesSource);

    public SplitCalibration Calibration => SplitCalibration.Parse(_page.SplitRuns);

    public void RecordOutcome(SplitOutcome outcome)
    {
        _page.SplitRuns = Calibration.With(outcome).Serialize();
        try { _page.SaveSettingsToStorage(); } catch { /* kept for this run even if saving fails */ }
    }

    public PriceSamples PriceSamples => PriceSamples.Parse(_page.ModelPriceSamples);

    public void RecordCosts(IReadOnlyList<ModelCostSample> samples)
    {
        var before = _page.ModelPriceSamples;
        var after = PriceSamples.With(samples);
        _page.ModelPriceSamples = after.Serialize();
        // Saved only while the list and Claude Code disagree: then the Prices
        // page needs the turns after a restart. A list that agrees costs no
        // write to the registry after every turn.
        if (_page.ModelPriceSamples == before) return;
        var prices = Prices;
        if (ModelPriceList.Families.Any(f => ModelPriceCheck.Check(prices, after, f) is { } m && !IsKept(m)))
            Save();
    }

    public bool IsKept(PriceMismatch mismatch) =>
        ModelPriceCheck.IsKept(mismatch, ModelPriceCheck.ParseKept(_page.ModelPricesKept));

    public void Keep(PriceMismatch mismatch)
    {
        var kept = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in ModelPriceCheck.ParseKept(_page.ModelPricesKept)) kept[k.Key] = k.Value;
        kept[mismatch.Family] = mismatch.Ratio;
        _page.ModelPricesKept = ModelPriceCheck.SerializeKept(kept);
        Save();
    }

    public void SavePrices(ModelPriceList prices)
    {
        Store(_page, prices);
        Save();
    }

    /// <summary>
    /// Puts a price list on the page. The built-in list is stored as empty,
    /// so a later release can update it. A kept difference was against the
    /// old prices of its family and goes when they change.
    /// </summary>
    internal static void Store(VsAgenticOptionsPage page, ModelPriceList prices)
    {
        var old = ModelPriceList.Parse(page.ModelPrices, page.ModelPricesSource);
        var same = prices.Serialize() == ModelPriceList.Defaults.Serialize();
        page.ModelPrices = same ? "" : prices.Serialize();
        page.ModelPricesSource = same && prices.Source == ModelPriceList.DefaultSource ? "" : prices.Source;

        var kept = ModelPriceCheck.ParseKept(page.ModelPricesKept)
            .Where(k => SamePrices(old.For(k.Key), prices.For(k.Key)))
            .ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
        page.ModelPricesKept = ModelPriceCheck.SerializeKept(kept);
    }

    private static bool SamePrices(ModelPrice a, ModelPrice b) =>
        a.Input == b.Input && a.Output == b.Output && a.CacheRead == b.CacheRead && a.CacheWrite == b.CacheWrite;

    private void Save()
    {
        try { _page.SaveSettingsToStorage(); } catch { /* kept for this run even if saving fails */ }
    }

    public bool IsOffFor(string projectFolder) =>
        Folders().Contains(Normalize(projectFolder), StringComparer.OrdinalIgnoreCase);

    public void TurnOffFor(string projectFolder)
    {
        var folder = Normalize(projectFolder);
        if (folder.Length == 0 || IsOffFor(folder)) return;
        Save(Folders().Concat(new[] { folder }).ToArray());
    }

    public void TurnOnFor(string projectFolder)
    {
        var folder = Normalize(projectFolder);
        Save(Folders().Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private string[] Folders() =>
        (_page.SplitSuggestionsOffProjects ?? "")
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(f => f.Length > 0)
            .ToArray();

    private void Save(string[] folders)
    {
        _page.SplitSuggestionsOffProjects = string.Join(";", folders);
        try { _page.SaveSettingsToStorage(); } catch { /* kept for this run even if saving fails */ }
    }

    private static string Normalize(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? "" : folder!.Trim().TrimEnd('\\', '/');
}
