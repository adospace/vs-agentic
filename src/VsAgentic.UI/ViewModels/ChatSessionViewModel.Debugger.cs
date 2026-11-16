using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The Debugger switch beside the attach buttons. It shows what Claude may
/// read from the debugger in this chat (nothing, the stack, or the stack and
/// values on request) and changes it for this chat. The level starts from
/// Tools > Options, where the default is Off: locals can hold data the user
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
    public IdeContextRegistry? IdeContext
    {
        get => _ideContext;
        set
        {
            _ideContext = value;
            ApplyDebuggerAccess();
        }
    }
    private IdeContextRegistry? _ideContext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebugging))]
    [NotifyPropertyChangedFor(nameof(IsDebuggerStopped))]
    [NotifyPropertyChangedFor(nameof(DebuggerAccessTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowDebuggerAccess))]
    private DebuggerMode _currentDebuggerMode = DebuggerMode.NotDebugging;

    /// <summary>
    /// What Claude may read from the debugger in this chat. Starts from the
    /// setting in Tools > Options, which is Off unless the user changed it;
    /// the switch changes it for this chat only.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDebuggerAccessOn))]
    [NotifyPropertyChangedFor(nameof(DebuggerAccessLabel))]
    [NotifyPropertyChangedFor(nameof(DebuggerAccessTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowDebuggerAccess))]
    [NotifyPropertyChangedFor(nameof(IsDebuggerAccessOffSelected))]
    [NotifyPropertyChangedFor(nameof(IsDebuggerWhereItStoppedSelected))]
    [NotifyPropertyChangedFor(nameof(IsDebuggerValuesSelected))]
    private DebuggerAccessLevel _debuggerAccess = DebuggerAccessLevel.Off;

    public bool IsDebuggerAccessOn => DebuggerAccess != DebuggerAccessLevel.Off;

    // The check marks in the switch's menu.
    public bool IsDebuggerAccessOffSelected => DebuggerAccess == DebuggerAccessLevel.Off;
    public bool IsDebuggerWhereItStoppedSelected => DebuggerAccess == DebuggerAccessLevel.WhereItStopped;
    public bool IsDebuggerValuesSelected => DebuggerAccess == DebuggerAccessLevel.WhereItStoppedAndValues;

    public bool IsDebugging => CurrentDebuggerMode != DebuggerMode.NotDebugging;

    /// <summary>Stopped is when Claude actually sees the stack and values.</summary>
    public bool IsDebuggerStopped => CurrentDebuggerMode == DebuggerMode.Stopped;

    /// <summary>
    /// The switch shows whenever Claude may read the debugger, so the user
    /// sees that before a debug session starts and can turn it off ahead of
    /// time; and while debugging, so access can be turned on when it is off.
    /// </summary>
    public bool ShowDebuggerAccess =>
        _debuggerModeSource is not null && (IsDebuggerAccessOn || IsDebugging);

    public string DebuggerAccessLabel => DebuggerAccess switch
    {
        // Named by what Claude sees, not by the debugger's terms: a user picks
        // by what leaves the machine.
        DebuggerAccessLevel.WhereItStopped => "Debugger: location",
        DebuggerAccessLevel.WhereItStoppedAndValues => "Debugger: location + values",
        _ => "Debugger: off",
    };

    public string DebuggerAccessTooltip
    {
        get
        {
            var access = DebuggerAccess switch
            {
                DebuggerAccessLevel.WhereItStopped =>
                    "Claude sees where the program stopped: the line, the call stack and the exception type,\n"
                    + "e.g. \"NullReferenceException at Program.cs:64\". It never sees your data.",
                DebuggerAccessLevel.WhereItStoppedAndValues =>
                    "Claude sees where the program stopped, and can ask for variables and the exception message,\n"
                    + "e.g. \"customer = null, count = 42\". You approve each request.",
                _ => "Claude sees nothing from the debugger in this chat.",
            };
            var state = !IsDebugging ? "No program is being debugged."
                : IsDebuggerStopped ? "The program is stopped."
                : "The program is running.";
            return access + "\n" + state + "\nClick to change it for this chat. The default is in Tools > Options > VsAgentic.";
        }
    }

    partial void OnDebuggerAccessChanged(DebuggerAccessLevel value) => ApplyDebuggerAccess();

    private void ApplyDebuggerAccess()
    {
        try { IdeContext?.SetDebuggerAccess(DebuggerAccess); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Could not switch debugger access"); }
    }

    private void OnDebuggerModeChanged()
    {
        var mode = _debuggerModeSource?.Mode ?? DebuggerMode.NotDebugging;
        Dispatch(() =>
        {
            CurrentDebuggerMode = mode;
            OnPropertyChanged(nameof(ShowDebuggerAccess));
        });
    }
}
