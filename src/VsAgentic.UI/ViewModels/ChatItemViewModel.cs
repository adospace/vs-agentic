using CommunityToolkit.Mvvm.ComponentModel;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.ViewModels;

// Notice: a one-line event from the extension rather than the model, e.g.
// "a background command failed". Shown in the open, never folded with steps.
public enum ChatItemType { User, Assistant, ToolStep, Thinking, Notice }

public partial class ChatItemViewModel : ObservableObject
{
    public ChatItemType Type { get; init; }
    public string? ToolName { get; init; }
    public string Title { get; init; } = "";

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private string? _body;

    [ObservableProperty]
    private OutputBodyMode _bodyMode = OutputBodyMode.Markdown;

    [ObservableProperty]
    private OutputItemStatus _status = OutputItemStatus.Pending;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _expanderTitle = "";

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsCompleted => !IsStreaming;

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(IsCompleted));
}
