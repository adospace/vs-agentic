using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// AGENTS.md and the Claude CLI. The CLI (2.1.277 and later) reads AGENTS.md
/// only in a project with no CLAUDE.md of its own. Once CLAUDE.md exists,
/// AGENTS.md is left out unless CLAUDE.md imports it with an <c>@</c> line.
/// A running CLI does not read CLAUDE.md again; the import takes effect in
/// the next process (CLI 2.1.325).
/// </summary>
public static class SharedInstructionsFile
{
    public const string FileName = "AGENTS.md";

    /// <summary>
    /// The AGENTS.md in <paramref name="workingDirectory"/> when it exists and
    /// is not among the files the CLI loaded; null otherwise.
    /// </summary>
    public static string? FindNotLoaded(string workingDirectory, IEnumerable<string> loadedPaths)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return null;

        var path = Path.Combine(workingDirectory, FileName);
        if (!File.Exists(path)) return null;

        var full = Normalize(path);
        return loadedPaths.Any(p => string.Equals(Normalize(p), full, StringComparison.OrdinalIgnoreCase))
            ? null
            : path;
    }

    /// <summary>
    /// The project CLAUDE.md to import AGENTS.md from: <c>CLAUDE.md</c> in the
    /// folder, else <c>.claude\CLAUDE.md</c>. The CLI reads both. When neither
    /// exists, a new one goes in the folder.
    /// </summary>
    public static string ChooseClaudeMd(string workingDirectory)
    {
        var root = Path.Combine(workingDirectory, "CLAUDE.md");
        var alt = Path.Combine(workingDirectory, ".claude", "CLAUDE.md");
        return !File.Exists(root) && File.Exists(alt) ? alt : root;
    }

    /// <summary>
    /// The import line for <paramref name="agentsMdPath"/> in
    /// <paramref name="claudeMdPath"/>. The CLI resolves it against the
    /// folder of the importing file.
    /// </summary>
    public static string ImportLine(string claudeMdPath, string agentsMdPath)
    {
        var from = new Uri(AppendSeparator(Path.GetDirectoryName(Path.GetFullPath(claudeMdPath))!));
        var to = new Uri(Path.GetFullPath(agentsMdPath));
        return "@" + Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
    }

    /// <summary>Whether the file already has the import line.</summary>
    public static bool HasImport(string claudeMdPath, string agentsMdPath)
    {
        if (!File.Exists(claudeMdPath)) return false;
        var line = ImportLine(claudeMdPath, agentsMdPath);
        return File.ReadAllLines(claudeMdPath).Any(l => string.Equals(l.Trim(), line, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds the import line at the end of <paramref name="claudeMdPath"/>, or
    /// creates the file with only that line. Keeps the file's line endings
    /// and byte order mark. Returns false when the line is already there.
    /// </summary>
    public static bool AddImport(string claudeMdPath, string agentsMdPath)
    {
        var line = ImportLine(claudeMdPath, agentsMdPath);

        if (!File.Exists(claudeMdPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(claudeMdPath))!);
            File.WriteAllText(claudeMdPath, line + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }

        if (HasImport(claudeMdPath, agentsMdPath)) return false;

        var bytes = File.ReadAllBytes(claudeMdPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var newline = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : Environment.NewLine;
        var builder = new StringBuilder(text);
        if (text.Length > 0)
        {
            if (!text.EndsWith("\n", StringComparison.Ordinal)) builder.Append(newline);
            builder.Append(newline);
        }
        builder.Append(line).Append(newline);

        File.WriteAllText(claudeMdPath, builder.ToString(), new UTF8Encoding(hasBom));
        return true;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return path; }
    }

    private static string AppendSeparator(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? directory
            : directory + Path.DirectorySeparatorChar;
}
