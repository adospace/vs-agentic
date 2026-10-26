using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The Debugger switch beside the attach buttons. It shows while Visual Studio
/// is debugging, so the user can see that Claude may read the program's state,
/// and turns that access off for this chat: locals can hold data the user
/// would rather not send.
/// </summary>
public partial class ChatSessionViewModel
{
    private IDebuggerModeSource? _debuggerModeSource;

    /// <summary>Set by the host once, after construction. Null hides the switch.</summary>
    public IDebuggerModeSource? DebuggerModeSource
    {
        get => _debuggerModeSource;
        set
        {
            if (_debuggerModeSource is not null)
                _debuggerModeSource.ModeChanged -= OnDebuggerModeChanged;

            _debuggerModeSource = value;

            if (_debuggerModeSource is not null)
                _debuggerModeSource.ModeChanged += OnDebuggerModeChanged;

            OnDebuggerModeChanged();
        }
    }

    /// <summary>The chat's tools, so the switch can turn the debugger ones off. Set by the host.</summary>
    public IdeContextRegistry? IdeContext { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugging))]
    [NotifyPropertyChangedFor(nameof(IsDebuggerStopped))]
    [NotifyPropertyChangedFor(nameof(DebuggerAccessTooltip))]
    private DebuggerMode _currentDebuggerMode = DebuggerMode.NotDebugging;

    /// <summary>On by default: the tool is useful only if Claude can call it unasked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebuggerAccessTooltip))]
    private bool _isDebuggerAccessOn = true;

    public bool IsDebugging => CurrentDebuggerMode != DebuggerMode.NotDebugging;

    /// <summary>Stopped is when Claude actually sees the stack and values.</summary>
    public bool IsDebuggerStopped => CurrentDebuggerMode == DebuggerMode.Stopped;

    public string DebuggerAccessTooltip
    {
        get
        {
            if (!IsDebuggerAccessOn)
                return "Debugger access is off for this chat.\n"
                       + "Claude cannot read where the program stopped, the call stack or variables.\n"
                       + "Click to turn it on.";

            var state = IsDebuggerStopped
                ? "The program is stopped: Claude can read where and why, the call stack, the exception,\n"
                  + "and on request arguments and locals. Values that look like secrets are withheld."
                : "The program is running. When it stops, Claude can read where and why, the call stack,\n"
                  + "the exception, and on request arguments and locals.";
            return "Debugger access is on for this chat.\n" + state + "\nClick to turn it off.";
        }
    }

    partial void OnIsDebuggerAccessOnChanged(bool value)
    {
        try { IdeContext?.SetGroupEnabled(IdeToolGroups.Debugger, value); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Could not switch debugger access"); }
    }

    private void OnDebuggerModeChanged()
    {
        var mode = _debuggerModeSource?.Mode ?? DebuggerMode.NotDebugging;
        Dispatch(() => CurrentDebuggerMode = mode);
    }
}
