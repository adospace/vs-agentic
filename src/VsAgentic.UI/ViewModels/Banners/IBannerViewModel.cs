namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// Marker interface for view models that represent an in-chat banner
/// (permission prompt, login prompt, AskUserQuestion card). The host's
/// banner ContentControl binds to a property of this type and selects
/// the right UserControl via DataTemplate by concrete type.
/// </summary>
public interface IBannerViewModel
{
}
