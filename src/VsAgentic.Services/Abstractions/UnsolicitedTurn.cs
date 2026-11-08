namespace VsAgentic.Services.Abstractions;

/// <summary>What made the CLI start a turn on its own.</summary>
public enum UnsolicitedTurnSource
{
    /// <summary>Nothing the CLI reported says why.</summary>
    Unknown,

    /// <summary>
    /// A check the model scheduled for itself (<c>CronCreate</c>, or a
    /// <c>/loop</c> wakeup). The CLI says so in <c>command_lifecycle</c>
    /// (<c>wakeup_source</c>).
    /// </summary>
    ScheduledCheck,

    /// <summary>
    /// A task in the background: a line from a running <c>Monitor</c>, or a
    /// task that has ended. The CLI marks the turn's result with
    /// <c>origin.kind</c> "task-notification", which comes only at the end of
    /// the turn; the source is read from the tasks when the turn starts.
    /// </summary>
    BackgroundTask,
}

/// <summary>
/// A turn the CLI started on its own, as known when it starts.
/// </summary>
/// <param name="Source">What started it.</param>
/// <param name="TaskDescription">
/// For <see cref="UnsolicitedTurnSource.BackgroundTask"/>: the description of
/// the task, or null when more than one task could have started the turn.
/// </param>
/// <param name="IsMonitor">The task is a <c>Monitor</c>.</param>
/// <param name="TaskRuns">
/// The task still runs: the turn reports one line of a <c>Monitor</c>, and
/// more such turns follow.
/// </param>
public sealed record UnsolicitedTurn(
    UnsolicitedTurnSource Source,
    string? TaskDescription = null,
    bool IsMonitor = false,
    bool TaskRuns = false)
{
    public static readonly UnsolicitedTurn Unknown = new UnsolicitedTurn(UnsolicitedTurnSource.Unknown);
    public static readonly UnsolicitedTurn ScheduledCheck = new UnsolicitedTurn(UnsolicitedTurnSource.ScheduledCheck);
}
