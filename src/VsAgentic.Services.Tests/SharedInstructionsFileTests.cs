using System;
using System.IO;
using System.Text;
using VsAgentic.Services.ClaudeCli;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// When the AGENTS.md notice shows, and what "Import AGENTS.md" writes. The
/// folder name has a space, as solution folders often do.
/// </summary>
public sealed class SharedInstructionsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vsagentic tests", Guid.NewGuid().ToString("N"));

    public SharedInstructionsFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Agents => Path.Combine(_folder, "AGENTS.md");
    private string RootClaude => Path.Combine(_folder, "CLAUDE.md");
    private string AltClaude => Path.Combine(_folder, ".claude", "CLAUDE.md");

    [Fact]
    public void NoAgentsMd_NothingToReport()
    {
        Assert.Null(SharedInstructionsFile.FindNotLoaded(_folder, new[] { RootClaude }));
    }

    [Fact]
    public void AgentsMdNotLoaded_IsReported()
    {
        File.WriteAllText(Agents, "x");
        Assert.Equal(Agents, SharedInstructionsFile.FindNotLoaded(_folder, new[] { RootClaude }));
    }

    [Fact]
    public void AgentsMdLoaded_PathComparedWithoutCase()
    {
        File.WriteAllText(Agents, "x");
        Assert.Null(SharedInstructionsFile.FindNotLoaded(_folder, new[] { Agents.ToUpperInvariant() }));
    }

    [Fact]
    public void ChoosesRootClaudeMd_ThenDotClaude_ThenNewRoot()
    {
        Assert.Equal(RootClaude, SharedInstructionsFile.ChooseClaudeMd(_folder));

        Directory.CreateDirectory(Path.GetDirectoryName(AltClaude)!);
        File.WriteAllText(AltClaude, "x");
        Assert.Equal(AltClaude, SharedInstructionsFile.ChooseClaudeMd(_folder));

        File.WriteAllText(RootClaude, "x");
        Assert.Equal(RootClaude, SharedInstructionsFile.ChooseClaudeMd(_folder));
    }

    [Fact]
    public void ImportLine_IsRelativeToTheImportingFile()
    {
        Assert.Equal("@AGENTS.md", SharedInstructionsFile.ImportLine(RootClaude, Agents));
        Assert.Equal("@../AGENTS.md", SharedInstructionsFile.ImportLine(AltClaude, Agents));
    }

    [Fact]
    public void AddImport_CreatesClaudeMd()
    {
        Assert.True(SharedInstructionsFile.AddImport(RootClaude, Agents));
        Assert.Equal("@AGENTS.md" + Environment.NewLine, File.ReadAllText(RootClaude));
        Assert.False(File.ReadAllBytes(RootClaude)[0] == 0xEF);
    }

    [Fact]
    public void AddImport_AppendsAfterABlankLine_KeepingLineEndings()
    {
        File.WriteAllText(RootClaude, "# Rules\n\nBe brief.");
        Assert.True(SharedInstructionsFile.AddImport(RootClaude, Agents));
        Assert.Equal("# Rules\n\nBe brief.\n\n@AGENTS.md\n", File.ReadAllText(RootClaude));
    }

    [Fact]
    public void AddImport_KeepsCrLfAndBom()
    {
        File.WriteAllText(RootClaude, "# Pravila\r\nKratko.\r\n", new UTF8Encoding(true));
        Assert.True(SharedInstructionsFile.AddImport(RootClaude, Agents));

        var bytes = File.ReadAllBytes(RootClaude);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, new[] { bytes[0], bytes[1], bytes[2] });
        Assert.Equal("# Pravila\r\nKratko.\r\n\r\n@AGENTS.md\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void AddImport_SecondTime_ChangesNothing()
    {
        File.WriteAllText(RootClaude, "  @AGENTS.md  \n");
        Assert.True(SharedInstructionsFile.HasImport(RootClaude, Agents));
        Assert.False(SharedInstructionsFile.AddImport(RootClaude, Agents));
        Assert.Equal("  @AGENTS.md  \n", File.ReadAllText(RootClaude));
    }

    [Fact]
    public void AddImport_InDotClaude_UsesParentPath()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AltClaude)!);
        File.WriteAllText(AltClaude, "x\n");
        Assert.True(SharedInstructionsFile.AddImport(AltClaude, Agents));
        Assert.Equal("x\n\n@../AGENTS.md\n", File.ReadAllText(AltClaude));
    }
}
