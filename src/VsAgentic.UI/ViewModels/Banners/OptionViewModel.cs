using CommunityToolkit.Mvvm.ComponentModel;

namespace VsAgentic.UI.ViewModels.Banners;

public partial class OptionViewModel : ObservableObject
{
    public string Label { get; }
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>1-based position within the question. Drives the Alt+N
    /// shortcut on the card and the number printed in front of the label.</summary>
    public int Ordinal { get; }

    /// <summary>"1." … "9.", or empty past the ninth option — Alt+N only
    /// reaches nine, so a tenth option must not advertise a key it lacks.</summary>
    public string OrdinalLabel => Ordinal is >= 1 and <= 9 ? $"{Ordinal}." : "";

    [ObservableProperty]
    private bool _isSelected;

    public OptionViewModel(string label, string description, int ordinal)
    {
        Label = label;
        Description = description;
        Ordinal = ordinal;
    }
}
