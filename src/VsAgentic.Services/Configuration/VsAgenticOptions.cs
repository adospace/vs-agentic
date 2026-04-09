namespace VsAgentic.Services.Configuration;

public class VsAgenticOptions
{
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// Path to the Claude CLI executable. Defaults to "claude" (assumes it's on PATH).
    /// </summary>
    public string ClaudeCliPath { get; set; } = "claude";

    /// <summary>
    /// Permission mode for the Claude CLI. Controls how tool permissions are handled.
    /// Defaults to <see cref="CliPermissionMode.Default"/>: every gated tool call is
    /// surfaced to the user via the in-process MCP permission helper, and the user
    /// approves/denies it through the chat banner. Use <see cref="CliPermissionMode.AcceptEdits"/>
    /// or <see cref="CliPermissionMode.BypassPermissions"/> as escape hatches.
    /// </summary>
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.Default;
}
