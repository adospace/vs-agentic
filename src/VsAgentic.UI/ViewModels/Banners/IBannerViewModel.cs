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

/// <summary>
/// A prompt that waits for the user's answer and can wait in the queue
/// behind another one: the permission banner and the question card.
/// </summary>
public interface IQueuedPrompt : IBannerViewModel
{
    /// <summary>Keeps the prompt from being answered for a moment after it
    /// comes up from the queue.</summary>
    void HoldInput(System.TimeSpan time);
}
