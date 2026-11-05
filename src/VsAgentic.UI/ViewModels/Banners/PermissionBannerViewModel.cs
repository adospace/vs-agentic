using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.ClaudeCli.Permissions;

namespace VsAgentic.UI.ViewModels.Banners;

public partial class PermissionBannerViewModel : ObservableObject, IQueuedPrompt
{
    private readonly PermissionRequest _request;
    private readonly Action<PermissionDecision> _onResolved;

    /// <summary>Broker id of the request this banner stands for. Lets the chat
    /// match a cancellation back to the banner on screen.</summary>
    public string RequestId => _request.Id;

    public string ToolName => _request.ToolName;

    /// <summary>The tool call this prompt is about; its step in the
    /// transcript has the same id.</summary>
    public string? ToolUseId => _request.ToolUseId;

    /// <summary>Title of the subagent that asks, or null for the main
    /// conversation. Can be filled in after the banner is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string? _askedBy;

    public string Header => AskedBy is { Length: > 0 } agent
        ? $"Subagent “{agent}” wants to use {_request.ToolName}"
        : $"Claude wants to use {_request.ToolName}";
    public string BodyText { get; }
    public bool HasBodyText => !string.IsNullOrEmpty(BodyText);

    /// <summary>Toggles the banner between the initial Allow/Deny/Other... row
    /// and the alternative-instructions input row.</summary>
    [ObservableProperty]
    private bool _isOtherMode;

    public bool IsInitialMode => !IsOtherMode;

    [ObservableProperty]
    private string _alternativeText = "";

    // The CLI takes rules back on an allow response and stops asking for
    // anything they match, so "for this session" is not modelled here: the
    // rules are handed over and the CLI's own rule engine does the matching.
    private readonly IReadOnlyList<PermissionRule> _sessionRules;

    /// <summary>Whether this request can be turned into rules at all.</summary>
    public bool CanAllowForSession => _sessionRules.Count > 0;

    /// <summary>
    /// Names every rule about to be granted, e.g. <c>Bash(cd:*) Bash(git push:*)</c>.
    /// This reads on the banner itself, next to the command: a compound command
    /// can carry several rules, and what a click grants has to be visible
    /// without hovering anything.
    /// </summary>
    public string SessionRulesText => string.Join("   ", _sessionRules.Select(r => r.Display));

    /// <summary>Says how long the rules last, which the line on the banner does not.</summary>
    public string AllowForSessionTooltip =>
        "The CLI stops asking for calls these rules match. They live in the CLI "
        + "process, so they are gone when it restarts and nothing is written to disk.";

    public PermissionBannerViewModel(PermissionRequest request, Action<PermissionDecision> onResolved)
    {
        _request = request;
        _onResolved = onResolved;
        BodyText = FormatBody(request);
        _sessionRules = PermissionRuleBuilder.Build(request.ToolName, request.Input);
    }

    // Set by the first decision. A second click or a held Enter must not
    // answer again: the next banner in the queue would be taken down unread.
    private bool _resolved;

    /// <summary>True for a moment after the banner comes up from the queue,
    /// while the buttons stay disabled. See <see cref="HoldInput"/>.</summary>
    [ObservableProperty]
    private bool _isInputHeld;

    partial void OnIsInputHeldChanged(bool value) => NotifyDecisionCommands();

    /// <summary>
    /// Keeps the buttons disabled for a moment. A banner that replaces an
    /// answered one appears under the pointer and the keyboard focus, so the
    /// second click of a double-click, or Enter held a little too long, would
    /// otherwise answer it before the user has read it.
    /// </summary>
    public void HoldInput(TimeSpan time)
    {
        IsInputHeld = true;
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsInputHeld = false;
        };
        timer.Start();
    }

    private bool CanDecide() => !_resolved && !IsInputHeld;

    private void Resolve(PermissionDecision decision)
    {
        if (!CanDecide()) return;
        _resolved = true;
        NotifyDecisionCommands();
        _onResolved(decision);
    }

    private void NotifyDecisionCommands()
    {
        AllowCommand.NotifyCanExecuteChanged();
        AllowForSessionCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        OtherCommand.NotifyCanExecuteChanged();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOtherModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInitialMode));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    partial void OnAlternativeTextChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Allow() => Resolve(PermissionDecision.Allow(InputJson()));

    /// <summary>Allow, and stop asking for calls of this shape until the CLI
    /// process exits.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void AllowForSession() =>
        Resolve(PermissionDecision.AllowForSession(InputJson(), _sessionRules));

    private string InputJson() =>
        _request.Input.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : _request.Input.GetRawText();

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Deny()
    {
        Resolve(PermissionDecision.Deny("User denied this action"));
    }

    /// <summary>Switches the banner into alt-input mode.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Other() => IsOtherMode = true;

    /// <summary>Returns to the initial Allow/Deny/Other row, preserving any
    /// text the user already typed in case they hit Other again.</summary>
    [RelayCommand]
    private void Back() => IsOtherMode = false;

    private bool CanSubmit() =>
        CanDecide() && IsOtherMode && !string.IsNullOrWhiteSpace(AlternativeText);

    /// <summary>Wire-level we send a Deny — that's how the CLI's MCP permission
    /// protocol surfaces "don't run this" — but the message wraps the user's
    /// alternative so Claude reads it as the tool_result and follows the new
    /// instructions instead of re-asking.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        var alt = (AlternativeText ?? "").Trim();
        Resolve(PermissionDecision.Deny(
            "The user declined to run this tool and asked you to do the following instead: " + alt));
    }

    private static string FormatBody(PermissionRequest request)
    {
        if (request.Input.ValueKind == JsonValueKind.Undefined) return "";
        try
        {
            if (request.Input.TryGetProperty("command", out var cmd))
                return cmd.GetString() ?? "";
            if (request.Input.TryGetProperty("file_path", out var fp))
                return fp.GetString() ?? "";
            if (request.Input.TryGetProperty("pattern", out var pat))
                return pat.GetString() ?? "";
            var raw = request.Input.GetRawText();
            return raw.Length > 400 ? raw.Substring(0, 400) + "..." : raw;
        }
        catch
        {
            return "";
        }
    }
}
