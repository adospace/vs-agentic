using System;
using System.Collections.Generic;
using System.Linq;
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

    // The CLI takes rules back on an allow response and stops asking for
    // anything they match, so remembering is not modelled here: the rules are
    // handed over and the CLI's own rule engine does the matching.
    private readonly IReadOnlyList<PermissionRule> _rules;
    private readonly IReadOnlyList<PermissionRule> _similarRules;

    /// <summary>Whether this request can be turned into rules at all.</summary>
    public bool CanAllowForSession => _rules.Count > 0;

    /// <summary>Whether the similar choices grant anything beyond the specific ones.</summary>
    public bool CanAllowSimilar { get; }

    // The tooltips name every rule about to be granted, e.g.
    // Bash(cd:*)  Bash(git push:*), so a compound command does not quietly
    // grant more than it appears to.
    public string AllowForSessionTooltip => $"Allow {Describe(_rules)} for the rest of this session";
    public string AllowAlwaysTooltip => $"Allow {Describe(_rules)} from now on, in every project. Written to ~/.claude/settings.json";
    public string AllowSimilarForSessionTooltip => $"Allow {Describe(_similarRules)} for the rest of this session";
    public string AllowSimilarAlwaysTooltip => $"Allow {Describe(_similarRules)} from now on, in every project. Written to ~/.claude/settings.json";

    public PermissionBannerViewModel(PermissionRequest request, Action<PermissionDecision> onResolved)
    {
        _request = request;
        _onResolved = onResolved;
        BodyText = FormatBody(request);
        _rules = PermissionRuleBuilder.Build(request.ToolName, request.Input);
        _similarRules = PermissionRuleBuilder.BuildSimilar(request.ToolName, request.Input);
        CanAllowSimilar = PermissionRuleBuilder.HasDistinctSimilar(request.ToolName, request.Input);
    }

    private static string Describe(IReadOnlyList<PermissionRule> rules) =>
        string.Join("  ", rules.Select(r => r.Display));

    partial void OnIsOtherModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInitialMode));
        SubmitCommand.NotifyCanExecuteChanged();
    }

    partial void OnAlternativeTextChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Allow() => _onResolved(PermissionDecision.Allow(InputJson()));

    /// <summary>Allow, and stop asking for calls of this shape until the CLI
    /// process exits.</summary>
    [RelayCommand]
    private void AllowForSession() => AllowWith(_rules, PermissionRuleScope.Session);

    /// <summary>
    /// Allow, and have the CLI write the rule to the user's own settings.
    ///
    /// User settings rather than the project's, for two reasons. The CLI
    /// ignores project settings until the workspace is trusted, so a rule
    /// written there can be silently inert. And a personal choice should not
    /// end up in a repository and travel into someone else's checkout. The
    /// cost is reach: the rule applies in every project, which the tooltip
    /// says.
    /// </summary>
    [RelayCommand]
    private void AllowAlways() => AllowWith(_rules, PermissionRuleScope.User);

    [RelayCommand]
    private void AllowSimilarForSession() => AllowWith(_similarRules, PermissionRuleScope.Session);

    [RelayCommand]
    private void AllowSimilarAlways() => AllowWith(_similarRules, PermissionRuleScope.User);

    private void AllowWith(IReadOnlyList<PermissionRule> rules, PermissionRuleScope scope) =>
        _onResolved(PermissionDecision.AllowWithRules(InputJson(), rules, scope));

    private string InputJson() =>
        _request.Input.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : _request.Input.GetRawText();

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
