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
    /// Defaults to AcceptEdits since the CLI runs non-interactively as a subprocess.
    /// </summary>
    public CliPermissionMode CliPermissionMode { get; set; } = CliPermissionMode.AcceptEdits;
}
