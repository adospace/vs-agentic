using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// AGENTS.md and the Claude CLI. The CLI (2.1.277 and later) reads AGENTS.md
/// only in a project with no CLAUDE.md of its own. Once CLAUDE.md exists,
/// AGENTS.md is left out unless CLAUDE.md imports it with an <c>@</c> line.
/// A running CLI does not read CLAUDE.md again; the import takes effect in
/// the next process (CLI 2.1.325).
///
/// Paths are compared as files, not as text: a common setup has CLAUDE.md as
/// a link to AGENTS.md (<c>ln -s AGENTS.md CLAUDE.md</c>). The CLI then
/// reports the CLAUDE.md path, and AGENTS.md is loaded all the same.
/// </summary>
public static class SharedInstructionsFile
{
    public const string FileName = "AGENTS.md";

    /// <summary>
    /// The AGENTS.md in <paramref name="workingDirectory"/> when it exists and
    /// is not among the files the CLI loaded, under its own name or through a
    /// link; null otherwise.
    /// </summary>
    public static string? FindNotLoaded(string workingDirectory, IEnumerable<string> loadedPaths)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return null;

        var path = Path.Combine(workingDirectory, FileName);
        if (!File.Exists(path)) return null;

        return loadedPaths.Any(p => SameFile(p, path)) ? null : path;
    }

    /// <summary>Whether the folder has a CLAUDE.md the CLI reads: in the folder or in <c>.claude</c>.</summary>
    public static bool HasClaudeMd(string workingDirectory) =>
        File.Exists(Path.Combine(workingDirectory, "CLAUDE.md"))
        || File.Exists(Path.Combine(workingDirectory, ".claude", "CLAUDE.md"));

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

    /// <summary>
    /// Whether the file already has the import line. False when it cannot be
    /// read as text (see <see cref="ReadText"/>).
    /// </summary>
    public static bool HasImport(string claudeMdPath, string agentsMdPath)
    {
        if (!File.Exists(claudeMdPath)) return false;
        var line = ImportLine(claudeMdPath, agentsMdPath);
        string text;
        try
        {
            text = ReadText(claudeMdPath, out _);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        return text.Split('\n').Any(l => string.Equals(l.Trim(), line, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds the import line at the end of <paramref name="claudeMdPath"/>, or
    /// creates the file with only that line. Keeps the file's encoding, byte
    /// order mark and line endings. Returns false when the line is already
    /// there.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// CLAUDE.md is AGENTS.md itself, through a link: the line would end up in
    /// AGENTS.md, where every other agent reads it.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The file has no byte order mark and is not valid UTF-8 (an ANSI code
    /// page, say). It is left as it is rather than rewritten with its
    /// non-ASCII characters lost.
    /// </exception>
    public static bool AddImport(string claudeMdPath, string agentsMdPath)
    {
        var line = ImportLine(claudeMdPath, agentsMdPath);

        if (!File.Exists(claudeMdPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(claudeMdPath))!);
            File.WriteAllText(claudeMdPath, line + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }

        if (SameFile(claudeMdPath, agentsMdPath))
            throw new InvalidOperationException(
                $"{Path.GetFileName(claudeMdPath)} is a link to {FileName}, so Claude already reads it. Nothing was changed.");

        var text = ReadText(claudeMdPath, out var encoding);
        if (text.Split('\n').Any(l => string.Equals(l.Trim(), line, StringComparison.OrdinalIgnoreCase)))
            return false;

        var newline = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : Environment.NewLine;
        var builder = new StringBuilder(text);
        if (text.Length > 0)
        {
            if (!text.EndsWith("\n", StringComparison.Ordinal)) builder.Append(newline);
            builder.Append(newline);
        }
        builder.Append(line).Append(newline);

        File.WriteAllText(claudeMdPath, builder.ToString(), encoding);
        return true;
    }

    /// <summary>
    /// The file as text, and the encoding to write it back with: UTF-8, with
    /// a byte order mark when the file has one. That is the only encoding the
    /// CLI reads CLAUDE.md in (2.1.332): a UTF-16 file reaches the model as
    /// garbled text, and an import line in it would not be read either.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is UTF-16 or UTF-32 (by its byte order mark), or not valid
    /// UTF-8 (an ANSI code page, say). It is left as it is.
    /// </exception>
    public static string ReadText(string path, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(path);
        var name = Path.GetFileName(path);

        // UTF-32 LE starts like UTF-16 LE (FF FE), so it goes first.
        (byte[] Bom, string Name)[] unicode =
        {
            (new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "UTF-32"),
            (new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "UTF-32"),
            (new byte[] { 0xFF, 0xFE }, "UTF-16"),
            (new byte[] { 0xFE, 0xFF }, "UTF-16"),
        };
        foreach (var (bom, kind) in unicode)
        {
            if (bytes.Length >= bom.Length && bom.SequenceEqual(bytes.Take(bom.Length)))
                throw new InvalidDataException(
                    $"{name} is saved as {kind}, and Claude Code reads it only as UTF-8, so it cannot read this file or an import in it. Save it as UTF-8 and try again.");
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom, throwOnInvalidBytes: true);
        try
        {
            encoding = utf8;
            return utf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"{name} is not UTF-8, so VsAgentic does not change it, and Claude Code cannot read its other characters either. Save it as UTF-8 and try again.", ex);
        }
    }

    /// <summary>
    /// Whether two paths name the same file: the same volume and file index,
    /// which a symbolic link, a hard link or a different spelling of the path
    /// all share. Falls back to comparing full paths when a file cannot be
    /// opened.
    /// </summary>
    public static bool SameFile(string a, string b)
    {
        if (string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase)) return true;
        return FileId(a) is { } idA && FileId(b) is { } idB && idA == idB;
    }

    private static (uint Volume, uint High, uint Low)? FileId(string path)
    {
        try
        {
            // Opening follows a symbolic link to its target. No access is
            // asked for, so a file open elsewhere still answers.
            using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
                FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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
