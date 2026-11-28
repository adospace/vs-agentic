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
    // Any one marker line left behind keeps the file open: an agent that
    // removed only the first line has not resolved it.
    [InlineData("<<<<<<< HEAD\nours only\n", true)]
    [InlineData("ours\n=======\ntheirs\n>>>>>>> feature\n", true)]
    [InlineData("x\n>>>>>>> feature\n", true)]
    [InlineData("x\n||||||| base\n", true)]
    [InlineData("x\n=======\r\n", true)]
    [InlineData("x\n>>>>>>>\n", true)]
    // Not markers: indented, longer than the marker size, or with text right after.
    [InlineData("  <<<<<<< HEAD\nx\n  =======\ny\n  >>>>>>> b\n", false)]
    [InlineData("<<<<<<<< HEAD\nx\n========\ny\n>>>>>>>> b\n", false)]
    [InlineData("Title\n========\n", false)]
    [InlineData("<<<<<<<x\n>>>>>>>y\n=======z\n", false)]
    public void HasConflictMarkers(string text, bool expected)
    {
        Assert.Equal(expected, GitConflicts.HasConflictMarkers(text));
    }

    [Theory]
    [InlineData("<<<<<<<<<< HEAD\nx\n==========\ny\n>>>>>>>>>> b\n", true)]
    [InlineData("x\n==========\ny\n", true)]
    // Seven is not a marker when conflict-marker-size is 10.
    [InlineData("<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> b\n", false)]
    public void HasConflictMarkers_UsesTheMarkerSize(string text, bool expected)
    {
        Assert.Equal(expected, GitConflicts.HasConflictMarkers(text, markerSize: 10));
    }

    [Theory]
    [InlineData("UD", "deleted by them")]
    [InlineData("DU", "deleted by us")]
    [InlineData("DD", "both deleted")]
    [InlineData("AU", "added by us")]
    [InlineData("UA", "added by them")]
    public void KeepOrDeleteConflict_NeedsResolvingWithoutMarkers(string code, string kind)
    {
        var file = new GitConflictFile("gone.cs", code, hasMarkers: false);
        Assert.True(file.IsKeepOrDeleteConflict);
        Assert.True(file.NeedsResolving);
        Assert.Equal(kind, file.Kind);
    }

    [Theory]
    [InlineData("UU")]
    [InlineData("AA")]
    public void BothSidesConflict_IsResolvedWithoutMarkers(string code)
    {
        var file = new GitConflictFile("a.cs", code, hasMarkers: false);
        Assert.False(file.IsKeepOrDeleteConflict);
        Assert.False(file.NeedsResolving);
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
        Assert.Contains("no `git checkout --ours`/`--theirs`, `git checkout -- <path>` or `git restore`", text);
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
    public async Task PathsAreLiteral_NotPatterns()
    {
        using var repo = TempRepo.TryCreate();
        if (repo is null) return;

        // Next.js names a route "[id].tsx"; as a pattern it also matches "i.tsx" and "d.tsx".
        Directory.CreateDirectory(Path.Combine(repo.Root, "pages"));
        repo.Write("pages/[id].tsx", "base\n");
        repo.Write("pages/i.tsx", "i\n");
        repo.Write("pages/d.tsx", "d\n");
        repo.Write("x[ab].cs", "one\n");
        repo.Write("xa.cs", "xa\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "feature/x");
        repo.Write("pages/[id].tsx", "feature\n");
        repo.Write("x[ab].cs", "feature\n");
        repo.Git("commit", "-am", "feature change");
        repo.Git("checkout", "main");
        repo.Git("rm", "-q", "--", ":(literal)pages/[id].tsx");
        repo.Write("x[ab].cs", "main\n");
        repo.Git("commit", "-am", "main change");
        repo.Git(allowFailure: true, "merge", "feature/x");

        // Delete removes "[id].tsx" only, not the files its pattern would match.
        Assert.True(await GitConflicts.SettleDeleteConflictAsync(repo.Root, "pages/[id].tsx", keep: false));
        Assert.True(File.Exists(Path.Combine(repo.Root, "pages", "i.tsx")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "pages", "d.tsx")));
        Assert.Equal(new[] { "pages/d.tsx", "pages/i.tsx" },
            repo.Output("ls-files", "--", "pages").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).OrderBy(l => l));

        // An unstaged edit in "xa.cs" stays unstaged when "x[ab].cs" is marked as resolved.
        repo.Write("xa.cs", "xa edited\n");
        repo.Write("x[ab].cs", "main and feature\n");
        Assert.Equal(new[] { "x[ab].cs" }, await GitConflicts.MarkResolvedAsync(repo.Root, new[] { "x[ab].cs" }));
        Assert.Equal("xa.cs", repo.Output("diff", "--name-only").Trim());

        repo.Git("merge", "--abort");
    }

    [Fact]
    public async Task ReadAsync_KeepsAFileOpenUntilEveryMarkerIsGone()
    {
        using var repo = TempRepo.TryCreate();
        if (repo is null) return;

        repo.Write(".gitattributes", "*.md conflict-marker-size=10\n");
        repo.Write("a.txt", "one\n");
        repo.Write("b.md", "one\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "feature/x");
        repo.Write("a.txt", "feature\n");
        repo.Write("b.md", "feature\n");
        repo.Git("commit", "-am", "feature change");
        repo.Git("checkout", "main");
        repo.Write("a.txt", "main\n");
        repo.Write("b.md", "main\n");
        repo.Git("commit", "-am", "main change");
        repo.Git(allowFailure: true, "merge", "feature/x");

        var state = await GitConflicts.ReadAsync(repo.Root);
        Assert.Equal(new[] { "a.txt", "b.md" }, state!.Open.Select(f => f.Path).OrderBy(p => p));
        Assert.StartsWith(new string('<', 10) + " ", File.ReadAllText(Path.Combine(repo.Root, "b.md")));

        // Only the first marker line removed: still open, and not staged when asked.
        repo.Write("a.txt", "main\n=======\nfeature\n>>>>>>> feature/x\n");
        repo.Write("b.md", "main\n" + new string('=', 10) + "\nfeature\n" + new string('>', 10) + " feature/x\n");
        state = await GitConflicts.ReadAsync(repo.Root);
        Assert.Equal(2, state!.Open.Count);
        Assert.Empty(await GitConflicts.MarkResolvedAsync(repo.Root, new[] { "a.txt", "b.md" }) ?? new[] { "failed" });

        repo.Write("a.txt", "main and feature\n");
        repo.Write("b.md", "main and feature\n");
        state = await GitConflicts.ReadAsync(repo.Root);
        Assert.Empty(state!.Open);

        repo.Git("merge", "--abort");
    }

    [Fact]
    public async Task ReadAsync_AFileOnlyOneSideHas_WaitsForKeepOrDelete()
    {
        using var repo = TempRepo.TryCreate();
        if (repo is null) return;

        repo.Write("a.txt", "one\ntwo\nthree\nfour\nfive\n");
        repo.Git("add", ".");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "feature/x");
        repo.Git("mv", "a.txt", "c.txt");
        repo.Git("commit", "-m", "rename to c");
        repo.Git("checkout", "main");
        repo.Git("mv", "a.txt", "b.txt");
        repo.Git("commit", "-m", "rename to b");
        repo.Git(allowFailure: true, "merge", "feature/x");

        var state = await GitConflicts.ReadAsync(repo.Root);

        // Rename/rename (git 2.56): "DD a.txt", "AU b.txt", "UA c.txt", no markers.
        // Before, AU and UA counted as edited and one click staged them.
        Assert.Equal(new[] { "AU b.txt", "DD a.txt", "UA c.txt" },
            state!.Files.Select(f => f.Code + " " + f.Path).OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(state.Files, f => Assert.True(f.IsKeepOrDeleteConflict));
        Assert.Equal(3, state.Open.Count);
        Assert.Empty(state.Resolved);
        Assert.Empty(await GitConflicts.MarkResolvedAsync(repo.Root, new[] { "b.txt", "c.txt" }) ?? new[] { "failed" });

        Assert.True(await GitConflicts.SettleDeleteConflictAsync(repo.Root, "b.txt", keep: true));
        Assert.True(await GitConflicts.SettleDeleteConflictAsync(repo.Root, "c.txt", keep: false));
        Assert.True(await GitConflicts.SettleDeleteConflictAsync(repo.Root, "a.txt", keep: false));
        Assert.Null(await GitConflicts.ReadAsync(repo.Root));
        Assert.True(File.Exists(Path.Combine(repo.Root, "b.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, "c.txt")));

        repo.Git("merge", "--abort");
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

        public void Git(bool allowFailure, params string[] args) => Run(allowFailure, args);

        public string Output(params string[] args) => Run(false, args);

        private string Run(bool allowFailure, string[] args)
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
            psi.StandardOutputEncoding = Encoding.UTF8;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && !allowFailure)
                throw new InvalidOperationException($"git {string.Join(" ", args)}: {err}");
            return output.Result;
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
