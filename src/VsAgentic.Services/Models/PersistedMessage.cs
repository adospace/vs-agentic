namespace VsAgentic.Services.Models;

public class PersistedMessage
{
    public int Ordinal { get; set; }
    public string ItemType { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? BodyMode { get; set; }
    public string? ExpanderTitle { get; set; }
    public string StatusText { get; set; } = "Success";

    /// <summary>
    /// Names of image files stored in the session's <c>images</c> folder. Only
    /// the names are kept here so <c>messages.json</c> stays small — the bytes
    /// would otherwise add megabytes per screenshot.
    /// </summary>
    public List<string>? ImageFileNames { get; set; }

    public DateTime CreatedUtc { get; set; }
}
