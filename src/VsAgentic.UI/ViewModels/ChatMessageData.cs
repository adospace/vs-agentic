using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// Lightweight DTO for passing message data to the ChatWebView JS layer.
/// Property names must match the JS expectations in chat-template.html.
/// </summary>
public class ChatMessageData
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Content { get; init; } = "";
    public string? ToolName { get; init; }
    public string Title { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public string ExpanderTitle { get; init; } = "";
    public string BodyMode { get; init; } = "Markdown";
    public string? Body { get; init; }
    public bool IsStreaming { get; init; }
}
