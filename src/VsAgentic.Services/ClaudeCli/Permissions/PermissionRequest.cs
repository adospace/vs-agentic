using System.Text.Json;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// A pending request from the Claude CLI for permission to use a tool.
/// Surfaced from the in-process MCP permission server via the named pipe.
/// </summary>
public sealed class PermissionRequest
{
    public string Id { get; }
    public string ToolName { get; }
    public JsonElement Input { get; }

    public PermissionRequest(string id, string toolName, JsonElement input)
    {
        Id = id;
        ToolName = toolName;
        Input = input;
    }
}

public enum PermissionBehavior
{
    Allow,
    Deny
}

/// <summary>
/// User's reply to a <see cref="PermissionRequest"/>. For Allow, supply the
/// (possibly modified) tool input as raw JSON. For Deny, supply a message
/// Claude will see in the tool_result.
/// </summary>
public sealed class PermissionDecision
{
    public PermissionBehavior Behavior { get; }
    public string? UpdatedInputJson { get; }
    public string? Message { get; }

    /// <summary>
    /// Rules the CLI should add so it stops asking for calls they match. Empty
    /// for a one-off allow. <see cref="Scope"/> says how long they last.
    /// </summary>
    public IReadOnlyList<PermissionRule> Rules { get; }

    public PermissionRuleScope Scope { get; }

    private PermissionDecision(
        PermissionBehavior behavior,
        string? updatedInputJson,
        string? message,
        IReadOnlyList<PermissionRule>? rules = null,
        PermissionRuleScope scope = PermissionRuleScope.Session)
    {
        Behavior = behavior;
        UpdatedInputJson = updatedInputJson;
        Message = message;
        Rules = rules ?? Array.Empty<PermissionRule>();
        Scope = scope;
    }

    public static PermissionDecision Allow(string updatedInputJson)
        => new PermissionDecision(PermissionBehavior.Allow, updatedInputJson, null);

    /// <summary>
    /// Allow this call and stop asking for anything matching <paramref name="rules"/>,
    /// for as long as <paramref name="scope"/> says.
    /// </summary>
    public static PermissionDecision AllowWithRules(
        string updatedInputJson,
        IReadOnlyList<PermissionRule> rules,
        PermissionRuleScope scope)
        => new PermissionDecision(PermissionBehavior.Allow, updatedInputJson, null, rules, scope);

    public static PermissionDecision Deny(string message)
        => new PermissionDecision(PermissionBehavior.Deny, null, message);
}

/// <summary>
/// How long an accepted rule lasts. Maps to the CLI's <c>destination</c>.
/// </summary>
public enum PermissionRuleScope
{
    /// <summary>Kept in the CLI process only; gone when it exits.</summary>
    Session,

    /// <summary>
    /// Written by the CLI to the user's own settings (<c>~/.claude/settings.json</c>),
    /// so it applies in every project until the user removes it.
    /// </summary>
    User
}

/// <summary>
/// One permission rule, in the form the CLI's rule engine already understands:
/// a tool name plus an optional specifier, e.g. <c>Bash</c> with <c>git log:*</c>
/// for "any git log command".
/// </summary>
public sealed class PermissionRule
{
    public string ToolName { get; }
    public string? RuleContent { get; }

    public PermissionRule(string toolName, string? ruleContent = null)
    {
        ToolName = toolName;
        RuleContent = string.IsNullOrWhiteSpace(ruleContent) ? null : ruleContent;
    }

    /// <summary>How the rule reads in settings files, e.g. <c>Bash(git log:*)</c>.</summary>
    public string Display => RuleContent is null ? ToolName : $"{ToolName}({RuleContent})";
}
