namespace VsAgentic.Services.Abstractions;

/// <summary>
/// State of a tool step whose work the CLI moved to the background, for
/// example a shell command started with <c>run_in_background</c>. The step
/// itself completes at once; this tracks the work it started.
/// </summary>
public enum BackgroundStepState
{
    Running,
    Completed,
    Failed,

    /// <summary>
    /// Ended without a result: stopped, or cut off when the CLI process ended.
    /// </summary>
    Stopped,
}
