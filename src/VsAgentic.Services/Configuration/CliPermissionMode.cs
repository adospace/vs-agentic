namespace VsAgentic.Services.Configuration;

/// <summary>
/// Permission mode passed to the Claude CLI via --permission-mode.
/// Controls how the CLI handles tool permission requests.
/// </summary>
public enum CliPermissionMode
{
    /// <summary>
    /// Auto-accept file edits (Edit, Write) but prompt for dangerous operations.
    /// Recommended for VS extension use since the CLI runs non-interactively.
    /// </summary>
    AcceptEdits,

    /// <summary>
    /// Bypass all permission checks. Only use in trusted/sandboxed environments.
    /// </summary>
    BypassPermissions,

    /// <summary>
    /// Default CLI permission behavior (prompts for all permissions).
    /// Not recommended — the CLI cannot prompt when running as a subprocess.
    /// </summary>
    Default
}
