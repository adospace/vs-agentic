namespace VsAgentic.Services.Configuration;

/// <summary>
/// Longest time a background Bash or PowerShell command may be given before
/// the CLI stops it.
///
/// Since 2.1.288 the CLI applies its background time limit only to sessions it
/// considers unattended: <c>-p</c>, the Agent SDK, CI and cloud. Terminal,
/// desktop app and VS Code sessions have no limit. VsAgentic drives the CLI
/// with <c>-p</c>, so a dev server or a watcher started in the background is
/// stopped after its <c>timeout</c> (default 30 min, at most 2 h), although a
/// person is at the keyboard. The CLI offers a host no switch to declare the
/// session attended; the only exemption is the VS Code entrypoint, which we
/// must not claim. What remains is <c>BASH_MAX_TIMEOUT_MS</c>: it raises the
/// most the model may ask for, and the Bash tool description tells the model
/// that number. The 30-minute default stays. The variable is not limited to
/// background commands: it raises the maximum of a foreground command as well.
/// </summary>
public enum BackgroundCommandLimit
{
    /// <summary>
    /// Leave <c>BASH_MAX_TIMEOUT_MS</c> alone: 2 hours at most, unless the
    /// environment Visual Studio started with already sets it.
    /// </summary>
    CliDefault,

    /// <summary>The model may keep a background command for up to 8 hours.</summary>
    EightHours,

    /// <summary>The model may keep a background command for up to 24 hours.</summary>
    OneDay,

    /// <summary>The model may keep a background command for up to 7 days.</summary>
    OneWeek,
}

public static class BackgroundCommandLimitExtensions
{
    /// <summary>
    /// Name of the CLI environment variable that caps a command's <c>timeout</c>.
    /// </summary>
    public const string MaxTimeoutVariable = "BASH_MAX_TIMEOUT_MS";

    /// <summary>
    /// The value for <see cref="MaxTimeoutVariable"/>, or <c>null</c> to leave it unset.
    /// </summary>
    public static string? ToMaxTimeoutMs(this BackgroundCommandLimit limit) => limit switch
    {
        BackgroundCommandLimit.EightHours => "28800000",
        BackgroundCommandLimit.OneDay => "86400000",
        BackgroundCommandLimit.OneWeek => "604800000",
        _ => null,
    };
}
