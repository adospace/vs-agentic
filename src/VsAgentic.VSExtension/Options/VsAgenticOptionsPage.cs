using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Configuration;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// Options page shown under Tools → Options → VsAgentic → General.
/// Settings are automatically persisted to the VS registry by DialogPage.
/// </summary>
[ComVisible(true)]
[Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d")]
public class VsAgenticOptionsPage : DialogPage
{
    // ── Backend ───────────────────────────────────────────────────────────

    [Category("Backend")]
    [DisplayName("Backend Mode")]
    [Description("ApiKey: direct Anthropic API calls (requires ANTHROPIC_API_KEY env var). ClaudeCli: uses your Claude subscription via the Claude Code CLI.")]
    [DefaultValue(BackendMode.ApiKey)]
    public BackendMode BackendMode { get; set; } = BackendMode.ApiKey;

    [Category("Backend")]
    [DisplayName("Anthropic API Key")]
    [Description("Your Anthropic API key. If set, overrides the ANTHROPIC_API_KEY environment variable. Only used in ApiKey mode.")]
    [DefaultValue("")]
    [PasswordPropertyText(true)]
    public string ApiKey { get; set; } = "";

    [Category("Backend")]
    [DisplayName("Claude CLI Path")]
    [Description("Path to the Claude Code CLI executable. Defaults to 'claude' (assumes it's on PATH). Only used in ClaudeCli mode.")]
    [DefaultValue("claude")]
    public string ClaudeCliPath { get; set; } = "claude";

    // ── Model ─────────────────────────────────────────────────────────────

    [Category("Model")]
    [DisplayName("Default Model")]
    [Description("The default Claude model ID used in ApiKey mode (e.g. claude-sonnet-4-6, claude-opus-4-6).")]
    [DefaultValue("claude-sonnet-4-20250514")]
    public string ModelId { get; set; } = "claude-sonnet-4-20250514";

    // ── Tools ─────────────────────────────────────────────────────────────

    [Category("Tools")]
    [DisplayName("Git Bash Path")]
    [Description("Path to the Git Bash executable used by the bash tool.")]
    [DefaultValue(@"C:\Program Files\Git\bin\bash.exe")]
    public string GitBashPath { get; set; } = @"C:\Program Files\Git\bin\bash.exe";

    [Category("Tools")]
    [DisplayName("Bash Timeout (seconds)")]
    [Description("Maximum time in seconds for bash tool commands before they are killed.")]
    [DefaultValue(30)]
    public int BashTimeoutSeconds { get; set; } = 30;

    [Category("Tools")]
    [DisplayName("Max Output Characters")]
    [Description("Maximum characters of tool output before truncation.")]
    [DefaultValue(5000)]
    public int MaxOutputChars { get; set; } = 5000;

    [Category("Tools")]
    [DisplayName("Max Read Lines")]
    [Description("Maximum number of lines returned by the read tool.")]
    [DefaultValue(200)]
    public int MaxReadLines { get; set; } = 200;

    // ── Advanced ──────────────────────────────────────────────────────────

    [Category("Advanced")]
    [DisplayName("System Prompt")]
    [Description("The system prompt sent to Claude. Change only if you know what you're doing.")]
    public string SystemPrompt { get; set; } = VsAgenticOptions.DefaultSystemPrompt;
}
