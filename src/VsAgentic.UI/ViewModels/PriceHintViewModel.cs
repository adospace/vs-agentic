using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The one-line strip above the message box that says Claude Code charged a
/// model other prices than the split estimate's price list, and lets the user
/// take them or keep the list. After "Use Claude Code's prices" it says what
/// changed and offers Undo.
/// </summary>
public partial class PriceHintViewModel : ObservableObject
{
    private readonly Action _accept;
    private readonly Action _keep;
    private readonly Action _dismiss;
    private readonly Action _undo;

    private PriceHintViewModel(Action accept, Action keep, Action dismiss, Action undo)
    {
        _accept = accept;
        _keep = keep;
        _dismiss = dismiss;
        _undo = undo;
    }

    public static PriceHintViewModel Suggest(string family, string text, string detail, Action accept, Action keep, Action dismiss) =>
        new PriceHintViewModel(accept, keep, dismiss, () => { })
        {
            Family = family,
            Text = text,
            Detail = detail,
            IsSuggestion = true
        };

    public static PriceHintViewModel Accepted(string family, string text, string detail, Action dismiss, Action undo) =>
        new PriceHintViewModel(() => { }, () => { }, dismiss, undo)
        {
            Family = family,
            Text = text,
            Detail = detail,
            IsSuggestion = false
        };

    /// <summary>The model family the strip is about.</summary>
    public string Family { get; private set; } = "";

    public string Text { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public bool IsSuggestion { get; private set; }
    public bool IsConfirmation => !IsSuggestion;

    [RelayCommand] private void Accept() => _accept();
    [RelayCommand] private void Keep() => _keep();
    [RelayCommand] private void Dismiss() => _dismiss();
    [RelayCommand] private void Undo() => _undo();
}
