using System.Collections.Generic;
using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.Services;
using Xunit;

namespace VsAgentic.Services.Tests;

public class TurnFileChangesTests
{
    private static FileChange Edit(string path, string before, string after, params FileChangeHunk[] hunks) => new()
    {
        FilePath = path,
        Before = before,
        After = after,
        Hunks = new List<FileChangeHunk>(hunks),
    };

    private static FileChangeHunk Hunk(int oldStart, params string[] lines)
    {
        var hunk = new FileChangeHunk { OldStart = oldStart, NewStart = oldStart, Lines = new List<string>(lines) };
        foreach (var line in lines)
        {
            if (line[0] != '+') hunk.OldLines++;
            if (line[0] != '-') hunk.NewLines++;
        }
        return hunk;
    }

    private static KeyValuePair<string, FileChange> Step(string id, FileChange change) => new(id, change);

    [Fact]
    public void Group_MergesEditsOfOneFile_InFirstChangedOrder()
    {
        var a1 = Edit(@"C:\p\a.cs", "x\n", "y\n", Hunk(1, "-x", "+y"));
        var b = Edit(@"C:\p\b.cs", "1\n", "1\n2\n", Hunk(1, " 1", "+2"));
        var a2 = Edit(@"C:\P\A.cs", "y\n", "z\nw\n", Hunk(1, "-y", "+z", "+w"));

        var entries = TurnFileChanges.Group(new[] { Step("s1", a1), Step("s2", b), Step("s3", a2) });

        Assert.Equal(2, entries.Count);
        Assert.Equal(@"C:\p\a.cs", entries[0].FilePath);
        Assert.Equal(new[] { "s1", "s3" }, entries[0].StepIds);
        Assert.Equal(3, entries[0].AddedLines);
        Assert.Equal(2, entries[0].RemovedLines);
        Assert.Equal(@"C:\p\b.cs", entries[1].FilePath);
        Assert.Equal(1, entries[1].AddedLines);
        Assert.Equal(0, entries[1].RemovedLines);
    }

    [Fact]
    public void Group_TakesNewFileFromFirstChange_AndCountsOmittedHunks()
    {
        var created = new FileChange { FilePath = @"C:\p\big.txt", IsNewFile = true, HunksOmitted = true, AddedLines = 9000 };
        var edited = Edit(@"C:\p\big.txt", "a\n", "b\n", Hunk(1, "-a", "+b"));

        var entries = TurnFileChanges.Group(new[] { Step("s1", created), Step("s2", edited) });

        var entry = Assert.Single(entries);
        Assert.True(entry.IsNewFile);
        Assert.Equal(9001, entry.AddedLines);
        Assert.Equal(1, entry.RemovedLines);
    }

    [Fact]
    public void Group_SkipsChangesWithoutPath()
    {
        Assert.Empty(TurnFileChanges.Group(new[] { Step("s1", new FileChange()) }));
    }

    [Fact]
    public void Diff_OneChange_IsTheStepDiff()
    {
        var change = Edit(@"C:\p\a.cs", "x\n", "y\n", Hunk(1, "-x", "+y"));
        var diff = TurnFileChanges.Diff(new[] { change });
        Assert.NotNull(diff);
        Assert.Equal("x\n", diff!.Before);
        Assert.Equal("y\n", diff.After);
        Assert.False(diff.ChangedPartsOnly);
    }

    [Fact]
    public void Diff_WholeFiles_ComparesBeforeFirstWithAfterLast()
    {
        var first = Edit(@"C:\p\a.cs", "1\n2\n3\n", "1\nX\n3\n", Hunk(2, "-2", "+X"));
        var second = Edit(@"C:\p\a.cs", "1\nX\n3\n", "1\nX\n3\n4\n", Hunk(3, " 3", "+4"));

        var diff = TurnFileChanges.Diff(new[] { first, second });

        Assert.NotNull(diff);
        Assert.Equal("1\n2\n3\n", diff!.Before);
        Assert.Equal("1\nX\n3\n4\n", diff.After);
        Assert.Equal(FileChanges.BeforeLabel, diff.BeforeLabel);
        Assert.Equal(FileChanges.AfterLabel, diff.AfterLabel);
        Assert.False(diff.ChangedPartsOnly);
    }

    [Fact]
    public void Diff_CreatedThenEdited_IsLabelledAsCreated()
    {
        var created = new FileChange { FilePath = @"C:\p\n.txt", IsNewFile = true, Before = "", After = "a\n" };
        var edited = Edit(@"C:\p\n.txt", "a\n", "b\n", Hunk(1, "-a", "+b"));

        var diff = TurnFileChanges.Diff(new[] { created, edited });

        Assert.Equal(FileChanges.NoFileLabel, diff!.BeforeLabel);
        Assert.Equal(FileChanges.CreatedLabel, diff.AfterLabel);
        Assert.Equal("", diff.Before);
        Assert.Equal("b\n", diff.After);
    }

    [Fact]
    public void Diff_WithoutWholeFiles_JoinsTheHunksOfEveryStep()
    {
        // As after a restore: only the hunks are saved.
        var first = new FileChange { FilePath = @"C:\p\a.cs", Hunks = { Hunk(2, "-2", "+X") } };
        var second = new FileChange { FilePath = @"C:\p\a.cs", Hunks = { Hunk(3, " 3", "+4") } };

        var diff = TurnFileChanges.Diff(new[] { first, second });

        Assert.NotNull(diff);
        Assert.True(diff!.ChangedPartsOnly);
        Assert.Contains("2\n", diff.Before);
        Assert.Contains("X\n", diff.After);
        Assert.Contains("3\n4\n", diff.After);
        Assert.DoesNotContain("4\n", diff.Before);
    }

    [Fact]
    public void Diff_MixedWholeAndHunks_UsesChangedPartsOfAll()
    {
        // The oldest step dropped its whole file under the session's limit.
        var first = new FileChange { FilePath = @"C:\p\a.cs", Hunks = { Hunk(1, "-a", "+b") } };
        var second = Edit(@"C:\p\a.cs", "b\nc\n", "b\nd\n", Hunk(2, "-c", "+d"));

        var diff = TurnFileChanges.Diff(new[] { first, second });

        Assert.True(diff!.ChangedPartsOnly);
        Assert.Equal(2, CountOf(diff.Before, "··· @@"));
    }

    [Fact]
    public void Diff_NothingKept_IsNull_AndCannotDiff()
    {
        var omitted = new FileChange { FilePath = @"C:\p\a.cs", HunksOmitted = true, AddedLines = 5 };
        Assert.Null(TurnFileChanges.Diff(new[] { omitted, omitted }));
        Assert.False(TurnFileChanges.CanDiff(new[] { omitted }));
        Assert.Null(TurnFileChanges.Diff(new FileChange[0]));
    }

    [Fact]
    public void CountLines_FromRealCliResult()
    {
        // tool_use_result of an Edit, as CLI 2.1.303 sends it.
        var json = """
        {"filePath":"C:\\p\\a.cs","oldString":"b","newString":"B\nB2","originalFile":"a\nb\nc\n",
         "structuredPatch":[{"oldStart":1,"oldLines":3,"newStart":1,"newLines":4,"lines":[" a","-b","+B","+B2"," c"]}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var change = FileChanges.FromToolUseResult(doc.RootElement)!;

        Assert.Equal((2, 1), TurnFileChanges.CountLines(change));
    }

    [Fact]
    public void Heading_CountsFiles()
    {
        Assert.Equal("Edited 1 file", TurnFileChanges.Heading(1));
        Assert.Equal("Edited 4 files", TurnFileChanges.Heading(4));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(part, i, System.StringComparison.Ordinal)) >= 0) { count++; i += part.Length; }
        return count;
    }
}
