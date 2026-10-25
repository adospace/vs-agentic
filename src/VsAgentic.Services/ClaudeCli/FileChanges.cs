using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Builds the two sides of a diff for Claude's file edits: from what the CLI
/// reports after an Edit, MultiEdit or Write call ran, and from the tool input
/// of a call that still waits for permission.
/// </summary>
/// <remarks>
/// The CLI hands the file over with <c>\n</c> line endings even when the file
/// on disk has CRLF, and matches <c>old_string</c> the same way. Every text
/// here is therefore normalized to <c>\n</c>, so both sides of a diff agree
/// and a CRLF file does not show every line as changed.
/// </remarks>
public static class FileChanges
{
    public const string BeforeLabel = "Before";
    public const string AfterLabel = "After";
    public const string CurrentLabel = "Current";
    public const string ProposedLabel = "Proposed";
    public const string NoFileLabel = "No file";
    public const string CreatedLabel = "Created";

    /// <summary>
    /// Reads <c>tool_use_result</c> of an Edit, MultiEdit or Write call. Null
    /// for any other tool result: the shape is told by <c>filePath</c> and
    /// <c>structuredPatch</c>, which only these tools send.
    /// </summary>
    public static FileChange? FromToolUseResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        var filePath = GetString(result, "filePath");
        if (string.IsNullOrEmpty(filePath)) return null;
        if (!result.TryGetProperty("structuredPatch", out var patch) || patch.ValueKind != JsonValueKind.Array) return null;

        var original = GetString(result, "originalFile");
        var change = new FileChange
        {
            FilePath = filePath!,
            IsNewFile = GetString(result, "type") == "create" || original is null,
            Hunks = ParseHunks(patch),
        };

        var before = Normalize(original ?? "");
        // Write sends the new content; Edit and MultiEdit only the patch.
        var content = GetString(result, "content");
        var after = content is not null ? Normalize(content) : ApplyHunks(before, change.Hunks);
        if (after is not null)
        {
            change.Before = before;
            change.After = after;
        }

        // A created file comes with no hunks. One that adds every line keeps
        // its content for a restored session too.
        if (change.Hunks.Count == 0 && change.IsNewFile && after is { Length: > 0 })
            change.Hunks.Add(AddedFileHunk(after));

        return change;
    }

    /// <summary>
    /// The diff for a step whose call has run: the whole file when the session
    /// still has it, otherwise the changed parts. Null when there is nothing to
    /// compare.
    /// </summary>
    public static FileDiffContent? ForStep(FileChange change)
    {
        if (change.Before is not null && change.After is not null)
        {
            return new FileDiffContent
            {
                FilePath = change.FilePath,
                Before = change.Before,
                After = change.After,
                BeforeLabel = change.IsNewFile ? NoFileLabel : BeforeLabel,
                AfterLabel = change.IsNewFile ? CreatedLabel : AfterLabel,
            };
        }

        if (change.Hunks.Count == 0) return null;

        var before = new StringBuilder();
        var after = new StringBuilder();
        foreach (var hunk in change.Hunks)
        {
            // The same line on both sides, so the viewer lines the hunks up
            // instead of showing the header as a change.
            var header = $"··· @@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@ ···\n";
            before.Append(header);
            after.Append(header);
            foreach (var line in hunk.Lines)
            {
                if (line.Length == 0) continue;
                var text = line.Substring(1);
                switch (line[0])
                {
                    case ' ': before.Append(text).Append('\n'); after.Append(text).Append('\n'); break;
                    case '-': before.Append(text).Append('\n'); break;
                    case '+': after.Append(text).Append('\n'); break;
                }
            }
        }

        return new FileDiffContent
        {
            FilePath = change.FilePath,
            Before = before.ToString(),
            After = after.ToString(),
            BeforeLabel = change.IsNewFile ? NoFileLabel : BeforeLabel,
            AfterLabel = change.IsNewFile ? CreatedLabel : AfterLabel,
            ChangedPartsOnly = true,
        };
    }

    /// <summary>
    /// The diff for a call that waits for permission: the file as it is now
    /// against the file with the call applied. Null when the tool does not
    /// edit files, or when the edit would not apply — the old text is not in
    /// the file, or it is there more than once without <c>replace_all</c>. The
    /// CLI refuses those calls too.
    /// </summary>
    /// <param name="readFile">Returns the file's text, or null when the file
    /// does not exist.</param>
    public static FileDiffContent? ForPermission(string toolName, JsonElement input, Func<string, string?> readFile)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var filePath = GetString(input, "file_path");
        if (string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath)) return null;

        try
        {
            var current = readFile(filePath!);
            var before = Normalize(current ?? "");
            string? after;

            switch (toolName)
            {
                case "Write":
                    var content = GetString(input, "content");
                    if (content is null) return null;
                    after = Normalize(content);
                    break;

                case "Edit":
                    after = ApplyEdit(before, current is null, input);
                    break;

                case "MultiEdit":
                    if (!input.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array) return null;
                    after = before;
                    var isNew = current is null;
                    foreach (var edit in edits.EnumerateArray())
                    {
                        after = ApplyEdit(after, isNew, edit);
                        if (after is null) break;
                        isNew = false;
                    }
                    break;

                default:
                    return null;
            }

            if (after is null) return null;
            return new FileDiffContent
            {
                FilePath = filePath!,
                Before = before,
                After = after,
                BeforeLabel = current is null ? NoFileLabel : CurrentLabel,
                AfterLabel = ProposedLabel,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies one <c>old_string</c> → <c>new_string</c> edit the way the CLI
    /// does. An empty <c>old_string</c> creates a file that does not exist yet.
    /// </summary>
    private static string? ApplyEdit(string text, bool fileIsNew, JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object) return null;
        var oldString = GetString(edit, "old_string");
        var newString = GetString(edit, "new_string");
        if (oldString is null || newString is null) return null;
        oldString = Normalize(oldString);
        newString = Normalize(newString);

        if (oldString.Length == 0)
            return fileIsNew ? newString : null;
        if (fileIsNew) return null;

        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        if (index < 0) return null;

        var replaceAll = edit.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;
        if (replaceAll) return text.Replace(oldString, newString);

        if (text.IndexOf(oldString, index + 1, StringComparison.Ordinal) >= 0) return null;
        return text.Substring(0, index) + newString + text.Substring(index + oldString.Length);
    }

    private static List<FileChangeHunk> ParseHunks(JsonElement patch)
    {
        var hunks = new List<FileChangeHunk>();
        foreach (var h in patch.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.Object) continue;
            var hunk = new FileChangeHunk
            {
                OldStart = GetInt(h, "oldStart"),
                OldLines = GetInt(h, "oldLines"),
                NewStart = GetInt(h, "newStart"),
                NewLines = GetInt(h, "newLines"),
            };
            if (h.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                {
                    // "\ No newline at end of file" says nothing the two
                    // sides of the diff do not already show.
                    if (line.ValueKind == JsonValueKind.String && line.GetString() is { Length: > 0 } s && s[0] != '\\')
                        hunk.Lines.Add(Normalize(s));
                }
            }
            hunks.Add(hunk);
        }
        return hunks;
    }

    private static FileChangeHunk AddedFileHunk(string content)
    {
        var lines = content.Split('\n');
        // The last element is empty when the content ends with a newline.
        var count = lines[lines.Length - 1].Length == 0 ? lines.Length - 1 : lines.Length;
        var hunk = new FileChangeHunk { OldStart = 0, OldLines = 0, NewStart = 1, NewLines = count };
        for (var i = 0; i < count; i++) hunk.Lines.Add("+" + lines[i]);
        return hunk;
    }

    /// <summary>
    /// Applies the hunks to <paramref name="original"/>. Null when a context or
    /// removed line does not match, so a patch for another version of the file
    /// never yields a wrong "after".
    /// </summary>
    internal static string? ApplyHunks(string original, IReadOnlyList<FileChangeHunk> hunks)
    {
        var lines = original.Split('\n');
        var result = new List<string>(lines.Length);
        var cursor = 0;

        foreach (var hunk in hunks)
        {
            // A hunk that only adds lines names the line it follows.
            var start = hunk.OldLines == 0 ? hunk.OldStart : hunk.OldStart - 1;
            if (start < cursor || start > lines.Length) return null;
            for (; cursor < start; cursor++) result.Add(lines[cursor]);

            foreach (var line in hunk.Lines)
            {
                var text = line.Substring(1);
                switch (line[0])
                {
                    case ' ':
                    case '-':
                        if (cursor >= lines.Length || lines[cursor] != text) return null;
                        if (line[0] == ' ') result.Add(text);
                        cursor++;
                        break;
                    case '+':
                        result.Add(text);
                        break;
                    default:
                        return null;
                }
            }
        }

        for (; cursor < lines.Length; cursor++) result.Add(lines[cursor]);
        return string.Join("\n", result);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;
}
