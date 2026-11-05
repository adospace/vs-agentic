using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// Follows the debugger through the public shell API: the mode (design, run,
/// break), a number for each break so a later read can tell it is looking at
/// a different one, and the last exception the debug engines reported.
///
/// The exception comes from <see cref="IDebugExceptionEvent2"/>, the event the
/// engines send when an exception stops the program. EnvDTE says only that
/// the break was an exception, not which one.
/// </summary>
internal sealed class DebuggerStateTracker : IDebuggerModeSource, IVsDebuggerEvents, IDebugEventCallback2, IDisposable
{
    private readonly IVsDebugger _debugger;
    private readonly object _gate = new();
    private uint _modeCookie;
    private bool _eventsAdvised;

    private DBGMODE _mode;
    private int _breakNumber;
    private DateTimeOffset? _breakStartedAt;
    private DebuggerException? _exception;

    // True between a stopping event and the switch to break mode it causes.
    private bool _stopCounted;

    public DebuggerStateTracker(IVsDebugger debugger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _debugger = debugger;

        var mode = new DBGMODE[1];
        if (ErrorHandler.Succeeded(debugger.GetMode(mode)))
            SetMode(mode[0]);

        ErrorHandler.ThrowOnFailure(debugger.AdviseDebuggerEvents(this, out _modeCookie));
        _eventsAdvised = ErrorHandler.Succeeded(debugger.AdviseDebugEventCallback(this));
    }

    public DebuggerState Read()
    {
        lock (_gate)
            return new DebuggerState(_mode, _breakNumber, _breakStartedAt, _mode == DBGMODE.DBGMODE_Break ? _exception : null);
    }

    int IVsDebuggerEvents.OnModeChange(DBGMODE dbgmodeNew)
    {
        SetMode(dbgmodeNew);
        return VSConstants.S_OK;
    }

    private void SetMode(DBGMODE mode)
    {
        // Edit and Continue is a flag on top of the mode, not a mode of its own.
        mode &= ~DBGMODE.DBGMODE_EncMask;

        lock (_gate)
        {
            if (mode == _mode) return;
            _mode = mode;

            if (mode == DBGMODE.DBGMODE_Break)
            {
                // Counted already when the event that stopped the program came first.
                if (!_stopCounted)
                    StartBreak();
                _stopCounted = false;
            }
            else
            {
                // The program moved on: the exception belonged to the break
                // that just ended. A new one arrives before the next break.
                _breakStartedAt = null;
                _exception = null;
                _stopCounted = false;
            }
        }

        ModeChanged?.Invoke();
    }

    public DebuggerMode Mode
    {
        get
        {
            lock (_gate)
                return _mode switch
                {
                    DBGMODE.DBGMODE_Break => DebuggerMode.Stopped,
                    DBGMODE.DBGMODE_Run => DebuggerMode.Running,
                    _ => DebuggerMode.NotDebugging,
                };
        }
    }

    public event Action? ModeChanged;

    // Called under _gate.
    private void StartBreak()
    {
        _breakNumber++;
        _breakStartedAt = DateTimeOffset.Now;
        _exception = null;
    }

    int IDebugEventCallback2.Event(
        IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread,
        IDebugEvent2 pEvent, ref Guid riidEvent, uint dwAttrib)
    {
        // Called for every debug event (module loads, output, steps), so the
        // tests must stay cheap.

        // A step, breakpoint, pause or exception. The mode stays Break across
        // a step, so the mode events alone would not number it as a new break.
        if (((enum_EVENTATTRIBUTES)dwAttrib & enum_EVENTATTRIBUTES.EVENT_STOPPING) != 0)
        {
            lock (_gate)
            {
                StartBreak();
                _stopCounted = _mode != DBGMODE.DBGMODE_Break;
            }
        }

        if (riidEvent != typeof(IDebugExceptionEvent2).GUID || pEvent is not IDebugExceptionEvent2 exceptionEvent)
            return VSConstants.S_OK;

        try
        {
            var info = new EXCEPTION_INFO[1];
            string? name = ErrorHandler.Succeeded(exceptionEvent.GetException(info)) ? info[0].bstrExceptionName : null;
            string? description = ErrorHandler.Succeeded(exceptionEvent.GetExceptionDescription(out var text)) ? text : null;

            lock (_gate)
                _exception = new DebuggerException(name, description, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VsAgentic: Could not read a debugger exception event: {ex.Message}");
        }

        return VSConstants.S_OK;
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_modeCookie != 0)
        {
            _debugger.UnadviseDebuggerEvents(_modeCookie);
            _modeCookie = 0;
        }

        if (_eventsAdvised)
        {
            _debugger.UnadviseDebugEventCallback(this);
            _eventsAdvised = false;
        }
    }
}

internal sealed class DebuggerState
{
    public DBGMODE Mode { get; }

    /// <summary>Goes up by one each time the program stops, including after each step.</summary>
    public int BreakNumber { get; }

    public DateTimeOffset? BreakStartedAt { get; }

    /// <summary>The last exception reported in this break; null outside break mode.</summary>
    public DebuggerException? Exception { get; }

    public DebuggerState(DBGMODE mode, int breakNumber, DateTimeOffset? breakStartedAt, DebuggerException? exception)
    {
        Mode = mode;
        BreakNumber = breakNumber;
        BreakStartedAt = breakStartedAt;
        Exception = exception;
    }
}

internal sealed class DebuggerException
{
    /// <summary>The exception type as the engine names it, e.g. System.NullReferenceException.</summary>
    public string? Name { get; }

    /// <summary>The engine's own text, usually with the message.</summary>
    public string? Description { get; }

    public DateTimeOffset ReportedAt { get; }

    public DebuggerException(string? name, string? description, DateTimeOffset reportedAt)
    {
        Name = name;
        Description = description;
        ReportedAt = reportedAt;
    }
}
