using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// Says that the solution's AGENTS.md is not loaded for the agent, and offers
/// to load it. Not a prompt: the chat goes on while it is shown, so it has its
/// own place above the input box and does not take <c>ActiveBanner</c>.
/// </summary>
public partial class SharedInstructionsBannerViewModel : ObservableObject
{
    private readonly Action _onImport;
    private readonly Action _onDismiss;

    public SharedInstructionsBannerViewModel(string agentName, Action onImport, Action onDismiss)
    {
        _onImport = onImport;
        _onDismiss = onDismiss;
        Title = $"AGENTS.md is not loaded for {agentName}";
        DetailMessage =
            $"This solution has instructions in AGENTS.md, which Copilot and other agents read. {agentName} reads CLAUDE.md, and CLAUDE.md does not import AGENTS.md.";
        SubText = "Import adds the line @AGENTS.md to CLAUDE.md, or creates CLAUDE.md with it. It applies from your next message.";
    }

    public string Title { get; }
    public string DetailMessage { get; }
    public string SubText { get; }

    [ObservableProperty]
    private bool _isWorking;

    [RelayCommand]
    private void Import()
    {
        if (IsWorking) return;
        IsWorking = true;
        _onImport();
    }

    [RelayCommand]
    private void Dismiss() => _onDismiss();
}
