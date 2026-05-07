using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using VsAgentic.Services.ClaudeCli.Questions;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// View model for a single question inside a <see cref="QuestionCardViewModel"/>.
/// Owns option selection (single- or multi-select), free-text "Other" entry,
/// and exposes <see cref="IsAnswered"/> + <see cref="GetAnswer"/> for the
/// containing card to aggregate at submit time.
/// </summary>
public partial class QuestionViewModel : ObservableObject
{
    public string QuestionText { get; }
    public string Header { get; }
    public bool MultiSelect { get; }
    public IReadOnlyList<OptionViewModel> Options { get; }

    /// <summary>RadioButton.GroupName binds to this so single-select is enforced
    /// natively by WPF (no VM-side sibling clearing needed).</summary>
    public string GroupName { get; } = "qg_" + Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _otherText = "";

    [ObservableProperty]
    private bool _isOtherSelected;

    public bool IsOtherActive =>
        IsOtherSelected && !string.IsNullOrEmpty((OtherText ?? "").Trim());

    public bool IsAnswered
    {
        get
        {
            if (IsOtherActive) return true;
            return Options.Any(o => o.IsSelected);
        }
    }

    /// <summary>Raised when <see cref="IsAnswered"/> may have changed — used
    /// by the parent card to refresh its submit-button label.</summary>
    public event EventHandler? AnsweredChanged;

    public QuestionViewModel(UserQuestion source)
    {
        QuestionText = source.Question;
        Header = source.Header;
        MultiSelect = source.MultiSelect;

        var opts = new List<OptionViewModel>(source.Options.Count);
        foreach (var o in source.Options)
        {
            var ovm = new OptionViewModel(o.Label, o.Description);
            ovm.PropertyChanged += OnOptionChanged;
            opts.Add(ovm);
        }
        Options = opts;
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OptionViewModel.IsSelected)) return;

        // Picking a real option always clears the "Other" override so the
        // submitted answer is unambiguous.
        if (sender is OptionViewModel opt && opt.IsSelected && IsOtherSelected)
            IsOtherSelected = false;

        OnPropertyChanged(nameof(IsAnswered));
        AnsweredChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnOtherTextChanged(string value)
    {
        // Auto-tick the Other selector when the user starts typing — matches
        // the prior builder's behavior so the typed text is actually counted.
        if (!string.IsNullOrEmpty((value ?? "").Trim()) && !IsOtherSelected)
            IsOtherSelected = true;

        OnPropertyChanged(nameof(IsOtherActive));
        OnPropertyChanged(nameof(IsAnswered));
        AnsweredChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsOtherSelectedChanged(bool value)
    {
        // Picking Other (single-select) clears the option list so submit
        // returns the typed text rather than a stale option label.
        if (value && !MultiSelect)
        {
            foreach (var o in Options) o.IsSelected = false;
        }

        OnPropertyChanged(nameof(IsOtherActive));
        OnPropertyChanged(nameof(IsAnswered));
        AnsweredChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetAnswer()
    {
        var typed = (OtherText ?? "").Trim();

        if (MultiSelect)
        {
            var picked = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (IsOtherActive) picked.Add(typed);
            return string.Join(", ", picked);
        }

        if (IsOtherActive) return typed;
        var first = Options.FirstOrDefault(o => o.IsSelected);
        return first?.Label ?? "";
    }
}
