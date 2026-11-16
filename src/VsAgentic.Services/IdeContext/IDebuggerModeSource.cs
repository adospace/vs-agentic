namespace VsAgentic.Services.IdeContext;

public enum DebuggerMode
{
    NotDebugging,
    Running,

    /// <summary>Stopped at a breakpoint, a step, an exception or a pause.</summary>
    Stopped,
}

/// <summary>
/// What Claude may read from the debugger. Set in Tools > Options, and per
/// chat with the Debugger switch.
/// </summary>
public enum DebuggerAccessLevel
{
    /// <summary>Nothing. The default: the user turns access on.</summary>
    Off,

    /// <summary>
    /// Where the program stopped: the line, the call stack and the exception
    /// type, without a prompt. No values.
    /// </summary>
    WhereItStopped,

    /// <summary>
    /// Where the program stopped, and on request variables and the exception
    /// message, each request through the permission banner.
    /// </summary>
    WhereItStoppedAndValues,
}

/// <summary>
/// Whether Visual Studio is debugging, for the chat's Debugger switch. Kept
/// apart from the provider so the chat can show it without the VS types.
/// </summary>
public interface IDebuggerModeSource
{
    DebuggerMode Mode { get; }

    /// <summary>Raised when the mode changes, on the thread that reported it.</summary>
    event Action? ModeChanged;
}
