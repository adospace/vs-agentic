using System;
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
