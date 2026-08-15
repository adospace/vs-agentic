using System;
using System.IO;
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

    /// <summary>
    /// Identity of this request for "allow for the rest of the session"
    /// purposes, or <c>null</c> when the request has no stable identity worth
    /// remembering.
    ///
    /// Only tools whose input carries a <c>file_path</c> get a key — Edit,
    /// Write, NotebookEdit and friends. Bash deliberately does not: its
    /// <c>command</c> differs on every call, so the only rule we could form
    /// would be "allow all Bash", which is far broader than the user agreed to.
    /// </summary>
    public string? SessionAllowKey { get; }

    public PermissionRequest(string id, string toolName, JsonElement input)
    {
        Id = id;
        ToolName = toolName;
        Input = input;
        SessionAllowKey = BuildSessionAllowKey(toolName, input);
    }

    private static string? BuildSessionAllowKey(string toolName, JsonElement input)
    {
        if (string.IsNullOrEmpty(toolName)) return null;
        if (input.ValueKind != JsonValueKind.Object) return null;
        if (!input.TryGetProperty("file_path", out var fp)) return null;
        if (fp.ValueKind != JsonValueKind.String) return null;

        var path = fp.GetString();
        if (string.IsNullOrWhiteSpace(path)) return null;

        // Normalise so "src\a.cs" and "src/a.cs" collapse to one rule. An
        // unnormalisable path still gets a key — worst case the user is asked
        // once more for a spelling we couldn't canonicalise.
        try { path = Path.GetFullPath(path); }
        catch (Exception) { }

        return toolName + "\0" + path;
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

    private PermissionDecision(PermissionBehavior behavior, string? updatedInputJson, string? message)
    {
        Behavior = behavior;
        UpdatedInputJson = updatedInputJson;
        Message = message;
    }

    public static PermissionDecision Allow(string updatedInputJson)
        => new PermissionDecision(PermissionBehavior.Allow, updatedInputJson, null);

    public static PermissionDecision Deny(string message)
        => new PermissionDecision(PermissionBehavior.Deny, null, message);
}
