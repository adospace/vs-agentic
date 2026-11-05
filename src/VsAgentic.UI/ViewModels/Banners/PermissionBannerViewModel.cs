using System;
using System.Text.Json;
using System.Threading.Tasks;
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

    private readonly Func<Task<string?>>? _viewDiff;

    /// <summary>True for a file edit whose change can be shown before Allow.</summary>
    public bool CanViewDiff => _viewDiff is not null;

    /// <summary>Why the last View diff opened nothing, e.g. the edit no
    /// longer matches the file. Empty when it opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffNotice))]
    private string _diffNotice = "";

    public bool HasDiffNotice => !string.IsNullOrEmpty(DiffNotice);

    /// <param name="viewDiff">Opens the diff of a file edit, read against the
    /// file at the moment of the click. Returns what to tell the user when it
    /// opened nothing. Null for a call that edits no file.</param>
    public PermissionBannerViewModel(
        PermissionRequest request,
        Action<PermissionDecision> onResolved,
        Func<Task<string?>>? viewDiff = null)
    {
        _request = request;
        _onResolved = onResolved;
        _viewDiff = viewDiff;
        BodyText = FormatBody(request);
    }

    /// <summary>Opens the file as it is now against the file with the edit
    /// applied. The banner stays: the user decides after looking.</summary>
    /// <remarks>Not a decision: it answers nothing, so it works while the
    /// answer buttons are held. It stops once the banner is answered, because
    /// the edit then runs or is refused and the diff would show a change that
    /// is no longer waiting.</remarks>
    [RelayCommand(CanExecute = nameof(CanOpenDiff))]
    private async Task ViewDiffAsync()
    {
        if (_viewDiff is null) return;
        DiffNotice = "";
        DiffNotice = await _viewDiff() ?? "";
    }

    private bool CanOpenDiff() => !_resolved;

    /// <summary>True once the user has answered. Checked again before the
    /// viewer opens: the file is read off the UI thread, and the answer may
    /// come while it is read.</summary>
    public bool IsAnswered => _resolved;

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
        ViewDiffCommand.NotifyCanExecuteChanged();
        _onResolved(decision);
    }

    private void NotifyDecisionCommands()
    {
        AllowCommand.NotifyCanExecuteChanged();
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
    private void Allow()
    {
        var inputJson = _request.Input.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : _request.Input.GetRawText();
        Resolve(PermissionDecision.Allow(inputJson));
    }

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
