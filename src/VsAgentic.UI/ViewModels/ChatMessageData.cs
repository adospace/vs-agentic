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

    /// <summary>
    /// For an assistant-type row that VsAgentic wrote itself, not Claude: the
    /// heading it gets in place of Claude's bullet (as "YOU" on a prompt).
    /// Null for Claude's own text.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The id of the Agent step a subagent's step goes under. Null for the
    /// main conversation.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Data URIs for images attached to a user message, rendered inside the
    /// prompt block. Null when the message has none.
    /// </summary>
    public List<string>? Images { get; init; }
}
