using System;
using System.IO;
using System.Linq;
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

    // ── CLAUDE.md as a link to AGENTS.md ──────────────────────────────────

    [Fact]
    public void ClaudeMdHardLinkedToAgentsMd_CountsAsLoaded_AndIsNotWritten()
    {
        File.WriteAllText(Agents, "# Shared\n");
        Assert.True(CreateHardLink(RootClaude, Agents, IntPtr.Zero));

        // The CLI reports the CLAUDE.md path only.
        Assert.Null(SharedInstructionsFile.FindNotLoaded(_folder, new[] { RootClaude }));
        Assert.Throws<InvalidOperationException>(() => SharedInstructionsFile.AddImport(RootClaude, Agents));
        Assert.Equal("# Shared\n", File.ReadAllText(Agents));
    }

    [Fact]
    public void ClaudeMdSymlinkedToAgentsMd_CountsAsLoaded_AndIsNotWritten()
    {
        File.WriteAllText(Agents, "# Shared\n");
        // Needs Developer Mode or the right to create links; skipped without.
        if (!CreateSymbolicLink(RootClaude, "AGENTS.md", AllowUnprivilegedCreate)) return;

        Assert.Null(SharedInstructionsFile.FindNotLoaded(_folder, new[] { RootClaude }));
        Assert.Throws<InvalidOperationException>(() => SharedInstructionsFile.AddImport(RootClaude, Agents));
        Assert.Equal("# Shared\n", File.ReadAllText(Agents));
    }

    [Fact]
    public void TwoSeparateFiles_AreNotTheSame()
    {
        File.WriteAllText(Agents, "a");
        File.WriteAllText(RootClaude, "a");
        Assert.False(SharedInstructionsFile.SameFile(Agents, RootClaude));
        Assert.True(SharedInstructionsFile.SameFile(Agents, Agents.ToUpperInvariant()));
    }

    // ── Encodings ─────────────────────────────────────────────────────────

    public static TheoryData<string> BomEncodings => new() { "utf-16LE", "utf-16BE", "utf-32LE", "utf-32BE" };

    private static Encoding WithBom(string name) => name switch
    {
        "utf-16LE" => new UnicodeEncoding(false, true),
        "utf-16BE" => new UnicodeEncoding(true, true),
        "utf-32LE" => new UTF32Encoding(false, true),
        _ => new UTF32Encoding(true, true),
    };

    [Theory]
    [MemberData(nameof(BomEncodings))]
    public void AddImport_KeepsAUnicodeEncoding(string name)
    {
        var encoding = WithBom(name);
        File.WriteAllText(RootClaude, "# Navodila\r\nŠumniki: čšž.\r\n", encoding);

        Assert.True(SharedInstructionsFile.AddImport(RootClaude, Agents));

        var bytes = File.ReadAllBytes(RootClaude);
        var preamble = encoding.GetPreamble();
        Assert.Equal(preamble, bytes.Take(preamble.Length));
        Assert.Equal("# Navodila\r\nŠumniki: čšž.\r\n\r\n@AGENTS.md\r\n",
            encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length));
        Assert.True(SharedInstructionsFile.HasImport(RootClaude, Agents));
        Assert.False(SharedInstructionsFile.AddImport(RootClaude, Agents));
    }

    [Fact]
    public void AddImport_RefusesAFileThatIsNotUtf8()
    {
        // Windows-1250 "č š ž": a lead byte followed by a space is not UTF-8.
        // ("čšž" together, E8 9A 9E, happens to be one valid UTF-8 character.)
        var ansi = new byte[] { (byte)'#', (byte)' ', 0xE8, (byte)' ', 0x9A, (byte)' ', 0x9E, (byte)'\r', (byte)'\n' };
        File.WriteAllBytes(RootClaude, ansi);

        Assert.Throws<InvalidDataException>(() => SharedInstructionsFile.AddImport(RootClaude, Agents));
        Assert.Equal(ansi, File.ReadAllBytes(RootClaude));
        Assert.False(SharedInstructionsFile.HasImport(RootClaude, Agents));
    }

    [Fact]
    public void HasClaudeMd_InFolderOrDotClaude()
    {
        Assert.False(SharedInstructionsFile.HasClaudeMd(_folder));
        Directory.CreateDirectory(Path.GetDirectoryName(AltClaude)!);
        File.WriteAllText(AltClaude, "x");
        Assert.True(SharedInstructionsFile.HasClaudeMd(_folder));
    }

    private const int AllowUnprivilegedCreate = 0x2;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string symlinkFileName, string targetFileName, int flags);

    [Fact]
    public void AddImport_InDotClaude_UsesParentPath()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AltClaude)!);
        File.WriteAllText(AltClaude, "x\n");
        Assert.True(SharedInstructionsFile.AddImport(AltClaude, Agents));
        Assert.Equal("x\n\n@../AGENTS.md\n", File.ReadAllText(AltClaude));
    }
}
