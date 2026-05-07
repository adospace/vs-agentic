using CommunityToolkit.Mvvm.ComponentModel;

namespace VsAgentic.UI.ViewModels.Banners;

public partial class OptionViewModel : ObservableObject
{
    public string Label { get; }
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    [ObservableProperty]
    private bool _isSelected;

    public OptionViewModel(string label, string description)
    {
        Label = label;
        Description = description;
    }
}
