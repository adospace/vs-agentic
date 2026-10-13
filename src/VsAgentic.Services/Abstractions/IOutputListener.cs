namespace VsAgentic.Services.Abstractions;

public enum OutputItemStatus
{
    Pending,
    Success,
    Error,
    Info
}

public enum OutputBodyMode
{
    Markdown,
    Html
}

public record OutputItem
{
    public required string Id { get; init; }
    public required string ToolName { get; init; }
    public required string Title { get; set; }
    public OutputItemStatus Status { get; set; } = OutputItemStatus.Pending;
    public OutputBodyMode BodyMode { get; set; } = OutputBodyMode.Markdown;
    public string? Body { get; set; }
    public string? Delta { get; set; }

    /// <summary>
    /// The id of the Agent step this step ran under, when a subagent produced
    /// it. Null for steps of the main conversation. Subagents can start
    /// subagents of their own, so the parent may itself have a parent.
    /// </summary>
    public string? ParentId { get; init; }
}

public interface IOutputListener
{
    /// <summary>
    /// Called when a new step begins. The item starts with Pending status.
    /// </summary>
    void OnStepStarted(OutputItem item);

    /// <summary>
    /// Called when a step's body or status is updated (e.g. streaming text).
    /// </summary>
    void OnStepUpdated(OutputItem item);

    /// <summary>
    /// Called when a step completes (status should be Success or Error).
    /// </summary>
    void OnStepCompleted(OutputItem item);
}
