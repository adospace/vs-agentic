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
    /// Permission mode the CLI switches to as it allows the call, e.g.
    /// <c>acceptEdits</c> when the user approves a plan and lets Claude edit
    /// without asking. Null leaves the mode to the CLI.
    /// </summary>
    public string? Mode { get; }

    private PermissionDecision(PermissionBehavior behavior, string? updatedInputJson, string? message, string? mode = null)
    {
        Behavior = behavior;
        UpdatedInputJson = updatedInputJson;
        Message = message;
        Mode = mode;
    }

    public static PermissionDecision Allow(string updatedInputJson)
        => new PermissionDecision(PermissionBehavior.Allow, updatedInputJson, null);

    /// <summary>Allow the call, and switch the session to <paramref name="mode"/>.</summary>
    public static PermissionDecision AllowAndSetMode(string updatedInputJson, string mode)
        => new PermissionDecision(PermissionBehavior.Allow, updatedInputJson, null, mode);

    public static PermissionDecision Deny(string message)
        => new PermissionDecision(PermissionBehavior.Deny, null, message);
}
