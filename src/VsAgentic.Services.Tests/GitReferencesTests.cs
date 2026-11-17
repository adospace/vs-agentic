using System;
using System.IO;
using System.Threading.Tasks;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Services;
using Xunit;

namespace VsAgentic.Services.Tests;

public class GitReferencesTests
{
    private const char Sep = '\u001f';

    [Fact]
    public void ParseLog_ReadsCommitsWithAndWithoutStat()
    {
        var output =
            $"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa{Sep}Fix the parser\n" +
            "\n" +
            " 3 files changed, 10 insertions(+), 2 deletions(-)\n" +
            $"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb{Sep}Merge branch 'x'\n" +
            $"cccccccccccccccccccccccccccccccccccccccc{Sep}Only adds\n" +
            "\n" +
            " 1 file changed, 4 insertions(+)\r\n";

        var commits = GitReferences.ParseLog(output);

        Assert.Equal(3, commits.Count);
        Assert.Equal("Commit aaaaaaa", commits[0].DisplayName);
        Assert.Equal("Fix the parser", commits[0].Title);
        Assert.Equal("3 files +10 −2", commits[0].Scope);

        // A merge has no stat line; the next commit's stat is not its own.
        Assert.Equal("Merge branch 'x'", commits[1].Title);
        Assert.Null(commits[1].FileCount);
        Assert.Equal("", commits[1].Scope);

        Assert.Equal("1 file +4 −0", commits[2].Scope);
    }

    [Fact]
    public void ParseLog_SubjectWithSeparatorLikeTextStaysWhole()
    {
        var commits = GitReferences.ParseLog($"dddddddd{Sep}Count 2 files changed, 1 insertion(+)\n");

        Assert.Single(commits);
        Assert.Equal("Count 2 files changed, 1 insertion(+)", commits[0].Title);
        Assert.Null(commits[0].FileCount);
    }

    [Theory]
    [InlineData(" 1 file changed, 1 deletion(-)", 1, 0, 1)]
    [InlineData(" 12 files changed, 340 insertions(+), 7 deletions(-)", 12, 340, 7)]
    [InlineData(" 2 files changed, 5 insertions(+)", 2, 5, 0)]
    public void ParseShortStat_ReadsCounts(string line, int files, int added, int removed)
    {
        var (f, a, r) = GitReferences.ParseShortStat(line);
        Assert.Equal(files, f);
        Assert.Equal(added, a);
        Assert.Equal(removed, r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a stat")]
    public void ParseShortStat_NotAStat(string? line)
    {
        Assert.Equal((null, null, null), GitReferences.ParseShortStat(line));
    }

    [Fact]
    public void ParsePullRequests_ReadsGhJson()
    {
        var json = """
            [{"additions":120,"changedFiles":4,"deletions":30,"number":42,"title":"Add the thing"},
             {"number":7,"title":null},
             {"title":"no number"}]
            """;

        var prs = GitReferences.ParsePullRequests(json);

        Assert.Equal(2, prs.Count);
        Assert.Equal("PR #42", prs[0].DisplayName);
        Assert.Equal("4 files +120 −30", prs[0].Scope);
        Assert.Contains("gh pr diff 42", prs[0].PromptLine);
        Assert.Contains("(Add the thing)", prs[0].PromptLine);
        Assert.Equal("", prs[1].Scope);
        Assert.DoesNotContain("()", prs[1].PromptLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no pull requests found")]
    [InlineData("{\"number\":1}")]
    public void ParsePullRequests_NotAList(string json)
    {
        Assert.Empty(GitReferences.ParsePullRequests(json));
    }

    [Fact]
    public void Branch_NamesBothSidesAndShortensRemoteBase()
    {
        var branch = ChatGitAttachment.Branch("feature/x", "origin/main", 2, 8, 1);

        Assert.Equal("feature/x vs main", branch.DisplayName);
        Assert.Contains("`git diff origin/main...feature/x`", branch.PromptLine);
        Assert.Equal("2 files +8 −1", branch.Scope);
    }

    [Fact]
    public void SameReference_SameSourcePath()
    {
        Assert.Equal(
            ChatGitAttachment.PullRequest(42, null).SourcePath,
            ChatGitAttachment.PullRequest(42, "Title", 1, 2, 3).SourcePath);
        Assert.NotEqual(
            ChatGitAttachment.Commit("abc", "x").SourcePath,
            ChatGitAttachment.Commit("abd", "x").SourcePath);
    }

    [Fact]
    public async Task OutsideARepository_GivesNothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vsagentic-no-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Empty(await GitReferences.GetRecentCommitsAsync(folder, 5));
            Assert.Null(await GitReferences.GetBranchChangesAsync(folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
