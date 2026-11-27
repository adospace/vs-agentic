using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Services;

/// <summary>
/// Reads the commits, the branch changes and the pull requests that the
/// <c>@</c> menu offers as attachments. Everything comes from <c>git</c> and
/// <c>gh</c> on the PATH, run in the session's folder. Nothing here is fatal:
/// a folder that is not a repository, a missing <c>gh</c> or one that is not
/// signed in only leave the menu with fewer entries.
/// </summary>
public static class GitReferences
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    // gh asks GitHub, which can be slow; the menu shows the rest meanwhile.
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(15);

    private const char Separator = '\u001f';

    /// <summary>The latest commits on the current branch, newest first.</summary>
    public static async Task<IReadOnlyList<ChatGitAttachment>> GetRecentCommitsAsync(
        string folder, int count, CancellationToken ct = default)
    {
        var output = await RunAsync(
            "git", folder, GitTimeout, ct,
            "log", "-n", count.ToString(CultureInfo.InvariantCulture),
            "--format=%H%x1f%s", "--shortstat", "--no-color").ConfigureAwait(false);

        return output is null ? Array.Empty<ChatGitAttachment>() : ParseLog(output);
    }

    /// <summary>
    /// The commits of the current branch that its base does not have, or null
    /// when there are none: on the base branch itself, on a detached HEAD, or
    /// with no base to compare against.
    /// </summary>
    public static async Task<ChatGitAttachment?> GetBranchChangesAsync(
        string folder, CancellationToken ct = default)
    {
        var branch = (await RunAsync("git", folder, GitTimeout, ct,
            "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(branch) || branch == "HEAD") return null;

        var baseRef = await FindBaseAsync(folder, ct).ConfigureAwait(false);
        if (baseRef is null || baseRef == branch) return null;

        var ahead = (await RunAsync("git", folder, GitTimeout, ct,
            "rev-list", "--count", baseRef + ".." + branch).ConfigureAwait(false))?.Trim();
        if (!int.TryParse(ahead, NumberStyles.None, CultureInfo.InvariantCulture, out var commits) || commits == 0)
            return null;

        var stat = await RunAsync("git", folder, GitTimeout, ct,
            "diff", "--shortstat", baseRef + "..." + branch).ConfigureAwait(false);
        var (files, added, removed) = ParseShortStat(stat);
        return ChatGitAttachment.Branch(branch!, baseRef, files ?? 0, added ?? 0, removed ?? 0);
    }

    /// <summary>The open pull requests of the repository, as gh lists them.</summary>
    public static async Task<IReadOnlyList<ChatGitAttachment>> GetOpenPullRequestsAsync(
        string folder, int count, CancellationToken ct = default)
    {
        var output = await RunAsync("gh", folder, GhTimeout, ct,
            "pr", "list", "--state", "open", "--limit", count.ToString(CultureInfo.InvariantCulture),
            "--json", "number,title,changedFiles,additions,deletions").ConfigureAwait(false);

        return output is null ? Array.Empty<ChatGitAttachment>() : ParsePullRequests(output);
    }

    /// <summary>
    /// One pull request by number, in any state. Null when gh cannot show it;
    /// the caller can still attach the bare number.
    /// </summary>
    public static async Task<ChatGitAttachment?> GetPullRequestAsync(
        string folder, int number, CancellationToken ct = default)
    {
        var output = await RunAsync("gh", folder, GhTimeout, ct,
            "pr", "view", number.ToString(CultureInfo.InvariantCulture),
            "--json", "number,title,changedFiles,additions,deletions").ConfigureAwait(false);

        return output is null ? null : ParsePullRequests("[" + output + "]").FirstOrDefault();
    }

    /// <summary>
    /// What the branch is compared against: the remote's default branch when
    /// the clone knows it, otherwise the first of main and master that exists.
    /// </summary>
    private static async Task<string?> FindBaseAsync(string folder, CancellationToken ct)
    {
        var remoteHead = (await RunAsync("git", folder, GitTimeout, ct,
            "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD").ConfigureAwait(false))?.Trim();
        if (!string.IsNullOrEmpty(remoteHead)) return remoteHead;

        foreach (var candidate in new[] { "origin/main", "origin/master", "main", "master" })
        {
            var found = await RunAsync("git", folder, GitTimeout, ct,
                "rev-parse", "--verify", "--quiet", candidate + "^{commit}").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(found)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Parses <c>git log --format=%H%x1f%s --shortstat</c>. A commit line holds
    /// the separator; the stat line, when there is one, follows it. A merge and
    /// an empty commit have none and keep a null size.
    /// </summary>
    public static IReadOnlyList<ChatGitAttachment> ParseLog(string output)
    {
        var result = new List<ChatGitAttachment>();
        string? hash = null;
        string? subject = null;

        void Flush(int? files, int? added, int? removed)
        {
            if (hash is not null)
                result.Add(ChatGitAttachment.Commit(hash, subject ?? "", files, added, removed));
            hash = null;
            subject = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sep = line.IndexOf(Separator);
            if (sep > 0)
            {
                Flush(null, null, null);
                hash = line.Substring(0, sep);
                subject = line.Substring(sep + 1);
                continue;
            }

            var (files, added, removed) = ParseShortStat(line);
            if (files is not null) Flush(files, added ?? 0, removed ?? 0);
        }
        Flush(null, null, null);
        return result;
    }

    private static readonly Regex ShortStat = new(
        @"(?<files>\d+) files? changed(?:, (?<added>\d+) insertions?\(\+\))?(?:, (?<removed>\d+) deletions?\(-\))?",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses " 3 files changed, 10 insertions(+), 2 deletions(-)". Either
    /// count is left out when it is zero. All null when the line is not a stat.
    /// </summary>
    public static (int? Files, int? Added, int? Removed) ParseShortStat(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, null, null);
        var match = ShortStat.Match(line);
        if (!match.Success) return (null, null, null);

        return (Number(match.Groups["files"]) ?? 0,
                Number(match.Groups["added"]) ?? 0,
                Number(match.Groups["removed"]) ?? 0);

        static int? Number(Group g) =>
            g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>Parses the JSON array of <c>gh pr list --json …</c>.</summary>
    public static IReadOnlyList<ChatGitAttachment> ParsePullRequests(string json)
    {
        var result = new List<ChatGitAttachment>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                if (pr.ValueKind != JsonValueKind.Object) continue;
                if (!pr.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n)) continue;

                result.Add(ChatGitAttachment.PullRequest(
                    n,
                    pr.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null,
                    Int(pr, "changedFiles"), Int(pr, "additions"), Int(pr, "deletions")));
            }
        }
        catch (JsonException)
        {
            // gh printed something other than JSON: an old gh, or a message.
        }
        return result;

        static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    /// <summary>
    /// Runs a command and returns its output, or null when it cannot start,
    /// fails, or takes too long. Prompts are turned off: there is no terminal
    /// to answer them, and a git asking for a password would only hang.
    /// </summary>
    private static async Task<string?> RunAsync(
        string exe, string folder, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GH_PROMPT_DISABLED"] = "1";
        psi.EnvironmentVariables["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.EnvironmentVariables["NO_COLOR"] = "1";

        Process process;
        try
        {
            process = Process.Start(psi)!;
            if (process is null) return null;
        }
        catch
        {
            // Not installed, or not on the PATH VS was started with.
            return null;
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds), ct);

            bool done;
            try
            {
                done = await exited.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                done = false;
            }

            if (!done)
            {
                try { process.Kill(); } catch { }
                return null;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout.Result : null;
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + "\"";
}
