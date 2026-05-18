using System;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.ClaudeCli.Permissions;

namespace VsAgentic.UI.ViewModels.Banners;

public partial class PermissionBannerViewModel : ObservableObject, IBannerViewModel
{
    private readonly PermissionRequest _request;
    private readonly Action<PermissionDecision> _onResolved;

    public string ToolName => _request.ToolName;
    public string Header => $"Claude wants to use {_request.ToolName}";
    public string BodyText { get; }
    public bool HasBodyText => !string.IsNullOrEmpty(BodyText);

    /// <summary>Toggles the banner between the initial Allow/Deny/Other... row
    /// and the alternative-instructions input row.</summary>
    [ObservableProperty]
    private bool _isOtherMode;

    public bool IsInitialMode => !IsOtherMode;

    [ObservableProperty]
    private string _alternativeText = "";

    public PermissionBannerViewModel(PermissionRequest request, Action<PermissionDecision> onResolved)
    {
        _request = request;
        _onResolved = onResolved;
        BodyText = FormatBody(request);
    }

    partial void OnIsOtherModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInitialMode));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    partial void OnAlternativeTextChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Allow()
    {
        var inputJson = _request.Input.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : _request.Input.GetRawText();
        _onResolved(PermissionDecision.Allow(inputJson));
    }

    [RelayCommand]
    private void Deny()
    {
        _onResolved(PermissionDecision.Deny("User denied this action"));
    }

    /// <summary>Switches the banner into alt-input mode.</summary>
    [RelayCommand]
    private void Other() => IsOtherMode = true;

    /// <summary>Returns to the initial Allow/Deny/Other row, preserving any
    /// text the user already typed in case they hit Other again.</summary>
    [RelayCommand]
    private void Back() => IsOtherMode = false;

    private bool CanSubmit() =>
        IsOtherMode && !string.IsNullOrWhiteSpace(AlternativeText);

    /// <summary>Wire-level we send a Deny — that's how the CLI's MCP permission
    /// protocol surfaces "don't run this" — but the message wraps the user's
    /// alternative so Claude reads it as the tool_result and follows the new
    /// instructions instead of re-asking.</summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        var alt = (AlternativeText ?? "").Trim();
        _onResolved(PermissionDecision.Deny(
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
