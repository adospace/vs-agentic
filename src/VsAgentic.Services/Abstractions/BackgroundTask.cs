namespace VsAgentic.Services.Abstractions;

/// <summary>
/// A task the CLI runs in the background, for example a shell command started
/// with <c>run_in_background</c>. Taken from the CLI's
/// <c>system/background_tasks_changed</c> event.
/// </summary>
public sealed record BackgroundTask(string Id, string Description, string? Type)
{
    /// <summary>
    /// Whether the task is a shell command. Only those write an output the
    /// user can read; a subagent's steps are in the transcript instead.
    /// </summary>
    public bool IsShell => Type == "local_bash";
}

/// <summary>
/// The end of what a background shell task has written: at most the last
/// 8 KiB. <see cref="Truncated"/> is set when there was more before it.
/// </summary>
public sealed record BackgroundTaskOutput(string Output, bool Truncated);
