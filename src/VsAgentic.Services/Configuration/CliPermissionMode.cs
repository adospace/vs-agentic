namespace VsAgentic.Services.Configuration;

/// <summary>
/// Permission mode passed to the Claude CLI via --permission-mode. Controls
/// which gated tool calls the CLI forwards to our in-process MCP permission
/// helper (which raises the chat banner) versus auto-accepting silently.
/// </summary>
public enum CliPermissionMode
{
    /// <summary>
    /// Auto-accept file-edit tools (Edit, Write, NotebookEdit, etc.) without
    /// prompting. All other gated tools (Bash, WebFetch, …) still surface a
    /// banner via the MCP permission helper.
    /// </summary>
    AcceptEdits,

    /// <summary>
    /// Auto-accept every gated tool call. The MCP permission helper is never
    /// invoked, so no banners appear. Only use in trusted/sandboxed
    /// environments — the agent can run arbitrary commands without confirmation.
    /// </summary>
    BypassPermissions,

    /// <summary>
    /// Prompt for every gated tool call. Each request is forwarded to the
    /// in-process MCP permission helper and surfaced to the user as an
    /// Allow / Deny banner in the chat. This is the safest mode and the
    /// default for the extension.
    /// </summary>
    Default
}
