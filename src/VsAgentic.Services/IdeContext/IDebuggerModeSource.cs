namespace VsAgentic.Services.IdeContext;

public enum DebuggerMode
{
    NotDebugging,
    Running,

    /// <summary>Stopped at a breakpoint, a step, an exception or a pause.</summary>
    Stopped,
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
