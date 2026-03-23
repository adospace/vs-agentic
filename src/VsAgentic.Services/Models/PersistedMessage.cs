namespace VsAgentic.Services.Models;

public class PersistedMessage
{
    public int Ordinal { get; set; }
    public string ItemType { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? ExpanderTitle { get; set; }
    public string StatusText { get; set; } = "Success";
    public DateTime CreatedUtc { get; set; }
}
