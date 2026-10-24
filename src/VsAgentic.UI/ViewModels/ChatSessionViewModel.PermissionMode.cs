using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The permission mode picker in the status bar under the chat input.
///
/// The picker changes this session only. Unlike the model and effort it is
/// not remembered: a Plan or Bypass picked for one task should not decide how
/// the next chat starts. That stays with Tools → Options.
/// </summary>
public partial class ChatSessionViewModel
{
    /// <summary>Per-session copies, because the CLI setting row relabels itself.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModeOptions { get; } =
        PermissionModeOption.Order.Select(m => new PermissionModeOption(m)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionModeTooltip))]
    private PermissionModeOption _selectedPermissionMode = null!;

    /// <summary>The mode the CLI last reported, in its own spelling.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionModeTooltip))]
    private string? _reportedPermissionMode;

    /// <summary>Set while the picker follows the service, so that is not taken for a pick.</summary>
    private bool _suppressPermissionModeApply;

    private void InitializePermissionMode(IChatService chatService)
    {
        chatService.PermissionModeChanged += OnChatServicePermissionModeChanged;
        chatService.PermissionModeRejected += OnChatServicePermissionModeRejected;
        SyncPermissionMode();
    }

    partial void OnSelectedPermissionModeChanged(PermissionModeOption value)
    {
        if (_suppressPermissionModeApply || _chatService is null || value is null) return;

        try
        {
            _chatService.ApplyPermissionMode(value.Mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VM] Could not apply permission mode change");
        }
    }

    private void OnChatServicePermissionModeChanged() => Dispatch(SyncPermissionMode);

    private void OnChatServicePermissionModeRejected(string error) => Dispatch(() =>
        AddNotice($"_The permission mode was not changed. The CLI said: {error}_"));

    /// <summary>Points the picker at what the service now asks for and reports.</summary>
    private void SyncPermissionMode()
    {
        if (_chatService is null) return;

        var mode = _chatService.PermissionMode;
        ReportedPermissionMode = _chatService.ReportedPermissionMode;

        // Only while that row is selected: with another mode picked, the mode
        // in force says nothing about what the CLI setting would give, and the
        // row has to stay findable as "CLI setting".
        var cliRow = PermissionModeOptions.First(o => o.Mode == CliPermissionMode.CliSetting);
        cliRow.DisplayName = mode == CliPermissionMode.CliSetting && ReportedPermissionMode is { } reported
            ? $"{PermissionModeOption.LabelFor(reported)} (CLI)"
            : PermissionModeOption.LabelFor(CliPermissionMode.CliSetting);

        _suppressPermissionModeApply = true;
        try
        {
            SelectedPermissionMode = PermissionModeOptions.FirstOrDefault(o => o.Mode == mode)
                ?? PermissionModeOptions[0];
        }
        finally
        {
            _suppressPermissionModeApply = false;
        }
    }

    public string PermissionModeTooltip
    {
        get
        {
            var inForce = ReportedPermissionMode is { } reported
                ? PermissionModeOption.LabelFor(reported)
                : SelectedPermissionMode is { Mode: not CliPermissionMode.CliSetting } selected
                    ? selected.DisplayName
                    : "not reported yet";

            return $"Permission mode: {inForce}\n"
                + "Takes effect at once, also during a reply. CLI setting and Bypass\n"
                + "apply after a restart before the next message; the conversation is kept.\n"
                + "Changes this chat only. New chats start in the mode set under\n"
                + "Tools → Options → VsAgentic.";
        }
    }
}

/// <summary>One row of the permission mode dropdown.</summary>
public partial class PermissionModeOption : ObservableObject
{
    public static readonly IReadOnlyList<CliPermissionMode> Order = new[]
    {
        CliPermissionMode.CliSetting,
        CliPermissionMode.Default,
        CliPermissionMode.AcceptEdits,
        CliPermissionMode.Plan,
        CliPermissionMode.Auto,
        CliPermissionMode.BypassPermissions,
    };

    public PermissionModeOption(CliPermissionMode mode)
    {
        Mode = mode;
        _displayName = LabelFor(mode);
    }

    public CliPermissionMode Mode { get; }

    [ObservableProperty]
    private string _displayName;

    /// <summary>
    /// "Manual" rather than "Default" for the CLI's default mode: the model
    /// picker's Default means "the CLI decides", which here is the CLI setting
    /// row. The CLI itself accepts "manual" as a name for it.
    /// </summary>
    public static string LabelFor(CliPermissionMode mode) => mode switch
    {
        CliPermissionMode.CliSetting => "CLI setting",
        CliPermissionMode.AcceptEdits => "Accept edits",
        CliPermissionMode.Plan => "Plan",
        CliPermissionMode.Auto => "Auto",
        CliPermissionMode.BypassPermissions => "Bypass",
        _ => "Manual",
    };

    /// <summary>A reported mode by its label, or its raw name for one this picker does not offer.</summary>
    public static string LabelFor(string cliValue) =>
        CliPermissionModeExtensions.FromCliValue(cliValue) is { } mode ? LabelFor(mode) : cliValue;

    public override string ToString() => DisplayName;
}
