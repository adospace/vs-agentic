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
    /// The step's own id and that of the Agent step it ran under, so a
    /// restored transcript can put a subagent's steps back under their Agent
    /// step. A step is saved when it completes, which for an Agent step is
    /// after all of its own steps; the ids are what tie them together again.
    /// Both null in sessions saved before subagent steps were shown.
    /// </summary>
    public string? StepId { get; set; }
    public string? ParentId { get; set; }

    /// <summary>
    /// Names of image files stored in the session's <c>images</c> folder. Only
    /// the names are kept here so <c>messages.json</c> stays small — the bytes
    /// would otherwise add megabytes per screenshot.
    /// </summary>
    public List<string>? ImageFileNames { get; set; }

    public DateTime CreatedUtc { get; set; }
}
