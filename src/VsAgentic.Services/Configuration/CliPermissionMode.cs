namespace VsAgentic.Services.Configuration;

/// <summary>
/// Permission mode for the Claude CLI. Controls which gated tool calls the CLI
/// forwards to our in-process MCP permission helper (which raises the chat
/// banner) versus deciding on its own.
///
/// Sent as <c>--permission-mode</c> at start-up and as a
/// <c>set_permission_mode</c> control request to a running process.
///
/// The members are stored by name in the VS settings, so existing names must
/// not change. <see cref="Default"/> is the CLI's <c>default</c> mode, which
/// the CLI now also calls <c>manual</c>.
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
    /// Auto-accept every gated tool call. Only a call an <c>ask</c> rule in the
    /// user's settings matches still reaches the MCP permission helper (CLI
    /// 2.1.296). Only use in trusted/sandboxed environments — the agent can run
    /// arbitrary commands without confirmation.
    /// </summary>
    BypassPermissions,

    /// <summary>
    /// Prompt for every gated tool call. Each request is forwarded to the
    /// in-process MCP permission helper and surfaced to the user as an
    /// Allow / Deny banner in the chat.
    /// </summary>
    Default,

    /// <summary>
    /// Send no <c>--permission-mode</c>, so the mode comes from the user's own
    /// CLI settings (<c>permissions.defaultMode</c>), as it does in a terminal.
    /// Without such a setting the CLI starts in <see cref="Default"/>.
    /// </summary>
    CliSetting,

    /// <summary>
    /// The model plans and does not edit files or run commands until the plan
    /// is approved.
    /// </summary>
    Plan,

    /// <summary>
    /// A classifier on Anthropic's side approves the tool calls it considers
    /// safe; the rest still surface a banner.
    /// </summary>
    Auto,
}

public static class CliPermissionModeExtensions
{
    /// <summary>
    /// The spelling the CLI expects, or null for <see cref="CliPermissionMode.CliSetting"/>,
    /// which sends nothing.
    /// </summary>
    public static string? ToCliValue(this CliPermissionMode mode) => mode switch
    {
        CliPermissionMode.AcceptEdits => "acceptEdits",
        CliPermissionMode.BypassPermissions => "bypassPermissions",
        CliPermissionMode.Plan => "plan",
        CliPermissionMode.Auto => "auto",
        CliPermissionMode.CliSetting => null,
        _ => "default",
    };

    /// <summary>
    /// The mode a value the CLI reported stands for. Null for a mode this
    /// extension does not offer (<c>dontAsk</c>, or one added later).
    /// </summary>
    public static CliPermissionMode? FromCliValue(string? value) => value switch
    {
        "default" or "manual" => CliPermissionMode.Default,
        "acceptEdits" => CliPermissionMode.AcceptEdits,
        "bypassPermissions" => CliPermissionMode.BypassPermissions,
        "plan" => CliPermissionMode.Plan,
        "auto" => CliPermissionMode.Auto,
        _ => null,
    };
}
