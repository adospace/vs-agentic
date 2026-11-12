using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The one-line strip above the message box. It suggests splitting the task
/// across agents, or, after "Never for this project", says how to get the
/// suggestions back.
/// </summary>
public partial class SplitHintViewModel : ObservableObject
{
    private readonly Action _showPlan;
    private readonly Action _dismiss;
    private readonly Action _never;
    private readonly Action _undo;

    private SplitHintViewModel(Action showPlan, Action dismiss, Action never, Action undo)
    {
        _showPlan = showPlan;
        _dismiss = dismiss;
        _never = never;
        _undo = undo;
    }

    public static SplitHintViewModel Suggest(string reason, Action showPlan, Action dismiss, Action never) =>
        new SplitHintViewModel(showPlan, dismiss, never, () => { })
        {
            Text = "This task could run faster and cheaper with several agents.",
            Detail = reason,
            IsSuggestion = true
        };

    public static SplitHintViewModel TurnedOff(string project, Action dismiss, Action undo) =>
        new SplitHintViewModel(() => { }, dismiss, () => { }, undo)
        {
            Text = $"No more agent suggestions for {project}.",
            Detail = "You can still split any task with the ⚡ button or /split. Turn suggestions back on in Tools › Options › VsAgentic › Agents.",
            IsSuggestion = false
        };

    public string Text { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public bool IsSuggestion { get; private set; }
    public bool IsConfirmation => !IsSuggestion;

    [RelayCommand] private void ShowPlan() => _showPlan();
    [RelayCommand] private void Dismiss() => _dismiss();
    [RelayCommand] private void Never() => _never();
    [RelayCommand] private void Undo() => _undo();
}
