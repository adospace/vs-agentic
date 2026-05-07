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

    public PermissionBannerViewModel(PermissionRequest request, Action<PermissionDecision> onResolved)
    {
        _request = request;
        _onResolved = onResolved;
        BodyText = FormatBody(request);
    }

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
