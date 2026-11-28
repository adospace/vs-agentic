using System;
using System.IO;
using System.Linq;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// Some tests here change PATH or CLAUDE_CONFIG_DIR for the whole process, so
/// nothing else may run beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>The parts of the setup check that read text and files, without starting a process.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClaudeSetupCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsagentic-setup-check-" + Guid.NewGuid().ToString("N"));

    public ClaudeSetupCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── ParseVersion ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("2.1.332 (Claude Code)", "2.1.332")]
    [InlineData("claude 10.20.30-beta\n", "10.20.30")]
    [InlineData("  2.1.296", "2.1.296")]
    public void ParseVersion_reads_the_first_version_number(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), ClaudeSetupCheck.ParseVersion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("command not found")]
    [InlineData("2.1")]
    public void ParseVersion_returns_null_without_a_version_number(string? text)
    {
        Assert.Null(ClaudeSetupCheck.ParseVersion(text));
    }

    // ── ResolveExecutable ─────────────────────────────────────────────────

    [Fact]
    public void ResolveExecutable_adds_exe_to_a_full_path_without_extension()
    {
        var exe = Touch("claude.exe");

        Assert.Equal(exe, ClaudeSetupCheck.ResolveExecutable(Path.Combine(_dir, "claude")));
    }

    [Fact]
    public void ResolveExecutable_keeps_a_full_path_with_extension()
    {
        var cmd = Touch("claude.cmd");

        Assert.Equal(cmd, ClaudeSetupCheck.ResolveExecutable(cmd));
    }

    [Fact]
    public void ResolveExecutable_returns_null_for_a_missing_file()
    {
        Assert.Null(ClaudeSetupCheck.ResolveExecutable(Path.Combine(_dir, "claude")));
    }

    [Fact]
    public void ResolveExecutable_finds_a_bare_name_on_PATH_including_a_quoted_entry()
    {
        var exe = Touch("claude.exe");
        var saved = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var missing = Path.Combine(_dir, "missing");
            Environment.SetEnvironmentVariable("PATH", $"{missing};;\"{_dir}\"");

            Assert.Equal(exe, ClaudeSetupCheck.ResolveExecutable("claude"));
            Assert.Null(ClaudeSetupCheck.ResolveExecutable("not-claude"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", saved);
        }
    }

    // ── CheckSettingsFiles ────────────────────────────────────────────────
    // The managed settings in %ProgramData% cannot be redirected, so the
    // assertions only look at items about the files a test wrote.

    [Fact]
    public void CheckSettingsFiles_accepts_comments_and_trailing_commas()
    {
        var project = WriteProjectSettings("settings.json",
            "{\n  // a comment\n  \"permissions\": { \"allow\": [\"Read\",], },\n}");

        var items = WithUserConfigDir(() => ClaudeSetupCheck.CheckSettingsFiles(_dir));

        Assert.DoesNotContain(items, i => i.Status != SetupCheckStatus.Ok && i.Detail.Contains(project));
        if (items.All(i => i.Status == SetupCheckStatus.Ok))
            Assert.Contains(items, i => i.Detail.Contains(project));
    }

    [Fact]
    public void CheckSettingsFiles_reports_invalid_JSON_with_its_line()
    {
        var project = WriteProjectSettings("settings.local.json", "{\n  \"permissions\": {\n    \"allow\": [\"Read\" \"Edit\"]\n  }\n}");

        var items = WithUserConfigDir(() => ClaudeSetupCheck.CheckSettingsFiles(_dir));

        var item = Assert.Single(items, i => i.Detail.Contains(project));
        Assert.Equal(SetupCheckStatus.Error, item.Status);
        Assert.Contains("line 3", item.Detail);
    }

    [Fact]
    public void CheckSettingsFiles_reports_a_file_that_is_not_an_object()
    {
        var project = WriteProjectSettings("settings.json", "[\"Read\"]");

        var items = WithUserConfigDir(() => ClaudeSetupCheck.CheckSettingsFiles(_dir));

        var item = Assert.Single(items, i => i.Detail.Contains(project));
        Assert.Equal(SetupCheckStatus.Error, item.Status);
    }

    [Fact]
    public void CheckSettingsFiles_reads_the_user_settings_from_CLAUDE_CONFIG_DIR()
    {
        var userDir = Path.Combine(_dir, "user");
        Directory.CreateDirectory(userDir);
        var user = Path.Combine(userDir, "settings.json");
        File.WriteAllText(user, "{ broken");

        var items = WithUserConfigDir(() => ClaudeSetupCheck.CheckSettingsFiles(workingDirectory: null), userDir);

        var item = Assert.Single(items, i => i.Detail.Contains(user));
        Assert.Equal(SetupCheckStatus.Error, item.Status);
    }

    [Fact]
    public void CheckSettingsFiles_without_a_folder_does_not_look_for_project_files()
    {
        WriteProjectSettings("settings.json", "{ broken");

        var items = WithUserConfigDir(() => ClaudeSetupCheck.CheckSettingsFiles(workingDirectory: null));

        Assert.DoesNotContain(items, i => i.Detail.Contains(_dir));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    private string WriteProjectSettings(string name, string content)
    {
        var claudeDir = Path.Combine(_dir, ".claude");
        Directory.CreateDirectory(claudeDir);
        var path = Path.Combine(claudeDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Runs <paramref name="check"/> with the user settings in a folder of the
    /// test, so the real <c>~/.claude/settings.json</c> does not take part.
    /// </summary>
    private T WithUserConfigDir<T>(Func<T> check, string? userDir = null)
    {
        userDir ??= Path.Combine(_dir, "empty-user");
        Directory.CreateDirectory(userDir);
        var saved = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", userDir);
            return check();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", saved);
        }
    }
}
