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

/// <summary>
/// Tool names of the steps that report on the session rather than on a tool
/// call: an API retry, a compaction, a notice from the CLI or a hook. The
/// transcript shows these as a line of their own instead of folding them into
/// the turn's activity row, and their title can change while they are open.
/// </summary>
public static class SessionLineSteps
{
    public const string Retry = "Retry";
    public const string Compact = "Compact";
    public const string CliMessage = "CliMessage";

    public static bool Contains(string? toolName) =>
        toolName is Retry or Compact or CliMessage;
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
