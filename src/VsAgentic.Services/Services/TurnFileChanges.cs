using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;

namespace VsAgentic.Services.Services;

/// <summary>
/// One file on the card that closes a turn: every step of the turn that
/// changed it, in order, and the lines they added and removed together.
/// </summary>
public sealed class TurnFileEntry
{
    public string FilePath { get; set; } = "";

    /// <summary>True when the turn's first change to the file created it.</summary>
    public bool IsNewFile { get; set; }

    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }

    /// <summary>Ids of the steps that changed the file, oldest first.</summary>
    public List<string> StepIds { get; } = new();
}

/// <summary>
/// Gathers what the steps of one turn did to files, for the card at the end
/// of the turn. Built only from <see cref="FileChange"/>, which a restored
/// session has too, so a reopened session shows the same card.
/// </summary>
public static class TurnFileChanges
{
    /// <summary>
    /// One entry per file, in the order the turn first changed them. Paths are
    /// compared without case, as on Windows.
    /// </summary>
    public static List<TurnFileEntry> Group(IEnumerable<KeyValuePair<string, FileChange>> changes)
    {
        var entries = new List<TurnFileEntry>();
        var byPath = new Dictionary<string, TurnFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in changes)
        {
            var change = pair.Value;
            if (string.IsNullOrEmpty(change.FilePath)) continue;
            if (!byPath.TryGetValue(change.FilePath, out var entry))
            {
                entry = new TurnFileEntry { FilePath = change.FilePath, IsNewFile = change.IsNewFile };
                byPath[change.FilePath] = entry;
                entries.Add(entry);
            }
            var (added, removed) = CountLines(change);
            entry.AddedLines += added;
            entry.RemovedLines += removed;
            entry.StepIds.Add(pair.Key);
        }
        return entries;
    }

    /// <summary>
    /// Lines one step added and removed: from its hunks, or from the counts it
    /// kept when its hunks were too large to save.
    /// </summary>
    public static (int Added, int Removed) CountLines(FileChange change)
    {
        if (change.HunksOmitted) return (change.AddedLines, change.RemovedLines);
        int added = 0, removed = 0;
        foreach (var hunk in change.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Length == 0) continue;
                if (line[0] == '+') added++;
                else if (line[0] == '-') removed++;
            }
        }
        return (added, removed);
    }

    /// <summary>
    /// The diff of one file over the whole turn. When the session still holds
    /// the whole file for every step, the file before the first change against
    /// the file after the last. Otherwise the changed parts of each step in
    /// turn. Null when nothing is left to compare.
    /// </summary>
    /// <param name="changes">The file's changes, oldest first.</param>
    public static FileDiffContent? Diff(IReadOnlyList<FileChange> changes)
    {
        if (changes.Count == 0) return null;
        if (changes.Count == 1) return FileChanges.ForStep(changes[0]);

        var first = changes[0];
        var last = changes[changes.Count - 1];
        if (changes.All(c => c.Before is not null && c.After is not null))
        {
            return new FileDiffContent
            {
                FilePath = last.FilePath,
                Before = first.Before!,
                After = last.After!,
                BeforeLabel = first.IsNewFile ? FileChanges.NoFileLabel : FileChanges.BeforeLabel,
                AfterLabel = first.IsNewFile ? FileChanges.CreatedLabel : FileChanges.AfterLabel,
            };
        }

        var before = new StringBuilder();
        var after = new StringBuilder();
        foreach (var change in changes)
        {
            if (FileChanges.ForHunks(change) is not { } part) continue;
            before.Append(part.Before);
            after.Append(part.After);
        }
        if (before.Length == 0 && after.Length == 0) return null;

        return new FileDiffContent
        {
            FilePath = last.FilePath,
            Before = before.ToString(),
            After = after.ToString(),
            BeforeLabel = first.IsNewFile ? FileChanges.NoFileLabel : FileChanges.BeforeLabel,
            AfterLabel = first.IsNewFile ? FileChanges.CreatedLabel : FileChanges.AfterLabel,
            ChangedPartsOnly = true,
        };
    }

    /// <summary>
    /// True when <see cref="Diff"/> has something to show for these changes.
    /// </summary>
    public static bool CanDiff(IReadOnlyList<FileChange> changes) =>
        changes.Any(c => (c.Before is not null && c.After is not null) || c.Hunks.Count > 0);

    /// <summary>"Edited 1 file", "Edited 3 files".</summary>
    public static string Heading(int fileCount) =>
        fileCount == 1 ? "Edited 1 file" : $"Edited {fileCount} files";
}
