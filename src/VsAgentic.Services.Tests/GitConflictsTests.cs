using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VsAgentic.Services.Services;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// Reading the conflicts Git leaves behind, and the request that asks the
/// agent to resolve them.
/// </summary>
public class GitConflictsTests
{
    [Fact]
    public void ParseStatus_KeepsOnlyUnmergedEntries()
    {
        var output = "UU src/a.cs\0M  src/b.cs\0AA new.txt\0UD gone.cs\0 M c.cs\0";
        var entries = GitConflicts.ParseStatus(output);
        Assert.Equal(new[] { ("UU", "src/a.cs"), ("AA", "new.txt"), ("UD", "gone.cs") }, entries);
    }

    [Fact]
    public void ParseStatus_SkipsTheOldPathOfARename()
    {
        // The old path "UU x" would otherwise read as an entry.
        var output = "R  new name.cs\0UU x\0DU kept.cs\0";
        var entries = GitConflicts.ParseStatus(output);
        Assert.Equal(new[] { ("DU", "kept.cs") }, entries);
    }

    [Fact]
    public void ParseStatus_KeepsSpacesAndNonAscii()
    {
        var entries = GitConflicts.ParseStatus("UU docs/Poročilo o delu.md\0");
        Assert.Equal("docs/Poročilo o delu.md", Assert.Single(entries).Path);
    }

    [Theory]
    [InlineData("<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feature\n", true)]
    [InlineData("<<<<<<< HEAD\r\nours\r\n||||||| base\r\nold\r\n=======\r\ntheirs\r\n>>>>>>> 1a2b3c\r\n", true)]
    [InlineData("<<<<<<<\nours\n=======\ntheirs\n>>>>>>>\n", true)]
    [InlineData("plain text\n", false)]
    // A Markdown heading underline and a lone marker are not a conflict block.
    [InlineData("Title\n=======\n", false)]
    [InlineData("<<<<<<< HEAD\nours only\n", false)]
    [InlineData("  <<<<<<< HEAD\nx\n=======\ny\n  >>>>>>> b\n", false)]
    [InlineData("<<<<<<<< HEAD\nx\n=======\ny\n>>>>>>>> b\n", false)]
    public void HasConflictMarkers(string text, bool expected)
    {
        Assert.Equal(expected, GitConflicts.HasConflictMarkers(text));
    }

    [Fact]
    public void DeleteConflict_NeedsResolvingWithoutMarkers()
    {
        var file = new GitConflictFile("gone.cs", "UD", hasMarkers: false);
        Assert.True(file.IsDeleteConflict);
        Assert.True(file.NeedsResolving);
        Assert.Equal("deleted by them", file.Kind);
    }

    [Fact]
    public void EditedFileWithoutMarkers_IsNoLongerOpen()
    {
        var state = new GitConflictState("C:\\repo", GitConflictOperation.Merge, "main", "Merge branch 'x'", new[]
        {
            new GitConflictFile("a.cs", "UU", hasMarkers: false),
            new GitConflictFile("b.cs", "UU", hasMarkers: true),
        });
        Assert.Equal("b.cs", Assert.Single(state.Open).Path);
    }

    [Fact]
    public void Signature_IgnoresOrderButNotState()
    {
        var a = new GitConflictState("r", GitConflictOperation.Merge, "main", "m", new[]
        {
            new GitConflictFile("a.cs", "UU", true), new GitConflictFile("b.cs", "UU", true),
        });
        var b = new GitConflictState("r", GitConflictOperation.Merge, "main", "m", new[]
        {
            new GitConflictFile("b.cs", "UU", true), new GitConflictFile("a.cs", "UU", true),
        });
        var c = new GitConflictState("r", GitConflictOperation.Merge, "main", "m", new[]
        {
            new GitConflictFile("b.cs", "UU", false), new GitConflictFile("a.cs", "UU", true),
        });
        Assert.Equal(a.Signature, b.Signature);
        // A file edited free of blocks is a new state: a dismissed notice comes back for it.
        Assert.NotEqual(a.Signature, c.Signature);
        Assert.Equal("b.cs", Assert.Single(c.Resolved).Path);
    }

    [Fact]
    public void Request_ListsOpenFilesAndForbidsStaging()
    {
        var state = new GitConflictState("r", GitConflictOperation.Merge, "main", "Merge branch 'feature/x'", new[]
        {
            new GitConflictFile("src/a.cs", "UU", true),
            new GitConflictFile("src/done.cs", "UU", false),
            new GitConflictFile("src/gone.cs", "DU", false),
        });

        var text = ConflictResolutionRequest.Build(state, "  keep our logging  ");

        Assert.Contains("A merge into `main` stopped on conflicts. Merge message: `Merge branch 'feature/x'`.", text);
        Assert.Contains("- `src/a.cs` (both modified)", text);
        Assert.Contains("- `src/gone.cs` (deleted by us)", text);
        Assert.DoesNotContain("src/done.cs", text);
        Assert.Contains("no `git add`", text);
        Assert.Contains("mark the files as resolved myself, in Visual Studio; do not give me git commands", text);
        Assert.DoesNotContain("merge editor", text);
        Assert.EndsWith("Also: keep our logging", text);
    }

    [Fact]
    public void Request_ExplainsOursAndTheirsInARebase()
    {
        var state = new GitConflictState("r", GitConflictOperation.Rebase, null, "feature/x, at 1a2b3c4 Add x", new[]
        {
            new GitConflictFile("a.cs", "UU", true),
        });
        var text = ConflictResolutionRequest.Build(state);
        Assert.Contains("A rebase of `feature/x, at 1a2b3c4 Add x` stopped on conflicts.", text);
        Assert.Contains("\"ours\" (:2) is the branch being rebased onto", text);
    }

    // ---- With a real git, in a temporary repository. Skipped silently when git is not on the PATH. ----

    [Fact]
    public async Task ReadAsync_FindsAMergeConflictAndItsResolution()
    {
        using var repo = TempRepo.TryCreate();
        if (repo is null) return;

        repo.Write("a.txt", "one\ntwo\nthree\n");
        repo.Write("gone.txt", "x\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "feature/x");
        repo.Write("a.txt", "one\nTWO feature\nthree\n");
        repo.Write("gone.txt", "changed\n");
        repo.Git("commit", "-am", "feature change");
        repo.Git("checkout", "main");
        repo.Write("a.txt", "one\nTWO main\nthree\n");
        repo.Git("rm", "-q", "gone.txt");
        repo.Git("commit", "-am", "main change");
        repo.Git(allowFailure: true, "merge", "feature/x");

        var state = await GitConflicts.ReadAsync(repo.Root);

        Assert.NotNull(state);
        Assert.Equal(GitConflictOperation.Merge, state!.Operation);
        Assert.Equal("main", state.CurrentBranch);
        Assert.Contains("feature/x", state.Incoming);
        Assert.Equal(new[] { "a.txt", "gone.txt" }, state.Open.Select(f => f.Path).OrderBy(p => p));
        Assert.Equal("DU", state.Files.Single(f => f.Path == "gone.txt").Code);

        // The agent edits the file; Git still lists it as unmerged, but it is no longer open.
        repo.Write("a.txt", "one\nTWO main and feature\nthree\n");
        state = await GitConflicts.ReadAsync(repo.Root);
        Assert.Equal(new[] { "gone.txt" }, state!.Open.Select(f => f.Path));
        Assert.Equal(2, state.Files.Count);

        // Marking as resolved stages only the edited file; the delete conflict
        // and a file asked for that holds markers again are left alone.
        var added = await GitConflicts.MarkResolvedAsync(repo.Root, new[] { "a.txt", "gone.txt" });
        Assert.Equal(new[] { "a.txt" }, added);
        state = await GitConflicts.ReadAsync(repo.Root);
        Assert.Equal("gone.txt", Assert.Single(state!.Files).Path);
        Assert.True(state.Files[0].CanKeep);

        // A delete conflict is settled only for a file that is in one.
        Assert.False(await GitConflicts.SettleDeleteConflictAsync(repo.Root, "a.txt", keep: false));
        Assert.True(await GitConflicts.SettleDeleteConflictAsync(repo.Root, "gone.txt", keep: false));
        Assert.Null(await GitConflicts.ReadAsync(repo.Root));
        Assert.False(File.Exists(Path.Combine(repo.Root, "gone.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "a.txt")));

        // Abort: no conflicts at all.
        repo.Git("merge", "--abort");
        Assert.Null(await GitConflicts.ReadAsync(repo.Root));
    }

    [Fact]
    public async Task ReadAsync_FindsARebaseAndTheBranchBeingRebased()
    {
        using var repo = TempRepo.TryCreate();
        if (repo is null) return;

        repo.Write("a.txt", "one\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "topic");
        repo.Write("a.txt", "topic\n");
        repo.Git("commit", "-am", "topic change");
        repo.Git("checkout", "main");
        repo.Write("a.txt", "main\n");
        repo.Git("commit", "-am", "main change");
        repo.Git("checkout", "topic");
        repo.Git(allowFailure: true, "rebase", "main");

        var state = await GitConflicts.ReadAsync(repo.Root);

        Assert.NotNull(state);
        Assert.Equal(GitConflictOperation.Rebase, state!.Operation);
        Assert.Null(state.CurrentBranch);
        Assert.StartsWith("topic, at ", state.Incoming);
        Assert.Contains("topic change", state.Incoming);
        Assert.Equal("a.txt", Assert.Single(state.Open).Path);

        repo.Git("rebase", "--abort");
    }

    [Fact]
    public async Task ReadAsync_NotARepository_IsNull()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vsagentic-noconflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(await GitConflicts.ReadAsync(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class TempRepo : IDisposable
    {
        public string Root { get; }

        private TempRepo(string root) => Root = root;

        public static TempRepo? TryCreate()
        {
            var root = Path.Combine(Path.GetTempPath(), "vsagentic-conflicts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repo = new TempRepo(root);
            try
            {
                repo.Git("init", "-q", "-b", "main");
            }
            catch
            {
                repo.Dispose();
                return null;
            }
            repo.Git("config", "user.name", "Test");
            repo.Git("config", "user.email", "test@example.invalid");
            repo.Git("config", "commit.gpgsign", "false");
            repo.Git("config", "core.autocrlf", "false");
            return repo;
        }

        public void Write(string path, string text) =>
            File.WriteAllText(Path.Combine(Root, path), text, new UTF8Encoding(false));

        public void Git(params string[] args) => Git(false, args);

        public void Git(bool allowFailure, params string[] args)
        {
            var psi = new ProcessStartInfo("git", string.Join(" ", args.Select(a => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a)))
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_EDITOR"] = "true";
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && !allowFailure)
                throw new InvalidOperationException($"git {string.Join(" ", args)}: {err}");
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch
            {
            }
        }
    }
}
