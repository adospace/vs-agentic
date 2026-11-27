using System.Diagnostics;
using System.Text;

namespace VsAgentic.Services.Services;

/// <summary>The Git operation that stopped on the conflicts.</summary>
public enum GitConflictOperation
{
    /// <summary>No merge, rebase, cherry-pick or revert: for example after <c>git stash pop</c>.</summary>
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

/// <summary>One file that Git lists as unmerged.</summary>
public sealed class GitConflictFile
{
    public GitConflictFile(string path, string code, bool hasMarkers)
    {
        Path = path;
        Code = code;
        HasMarkers = hasMarkers;
    }

    /// <summary>Relative to the repository root, with forward slashes.</summary>
    public string Path { get; }

    /// <summary>The two letters of <c>git status --porcelain</c>: UU, AA, DU, UD, AU, UA, DD.</summary>
    public string Code { get; }

    /// <summary>
    /// True while the file still holds a conflict block. Only both-modified and
    /// both-added files get one; a binary file never does.
    /// </summary>
    public bool HasMarkers { get; }

    /// <summary>
    /// Only one side has the file: one side deleted it (DU, UD, DD), or added
    /// it while the other side renamed or deleted it (AU, UA). There is no
    /// block to edit; the choice is keep or delete. Git writes no markers into
    /// such a file, so an untouched one would otherwise look resolved.
    /// </summary>
    public bool IsKeepOrDeleteConflict => Code is "DU" or "UD" or "DD" or "AU" or "UA";

    /// <summary>
    /// True until someone has made a choice for the file. An edited file whose
    /// blocks are all gone is resolved in the working folder, though Git still
    /// lists it as unmerged until the user accepts the merge.
    /// </summary>
    public bool NeedsResolving => IsKeepOrDeleteConflict || HasMarkers;

    /// <summary>The kind of conflict in the words of <c>git status</c>.</summary>
    public string Kind => GitConflicts.DescribeCode(Code);

    /// <summary>
    /// The file is still in the working folder, so it can be kept. False when
    /// both sides deleted it. Read when the state is read.
    /// </summary>
    public bool CanKeep { get; init; } = true;
}

/// <summary>The unmerged files of a repository and what stopped on them.</summary>
public sealed class GitConflictState
{
    public GitConflictState(
        string repositoryRoot,
        GitConflictOperation operation,
        string? currentBranch,
        string? incoming,
        IReadOnlyList<GitConflictFile> files)
    {
        RepositoryRoot = repositoryRoot;
        Operation = operation;
        CurrentBranch = currentBranch;
        Incoming = incoming;
        Files = files;
    }

    public string RepositoryRoot { get; }
    public GitConflictOperation Operation { get; }

    /// <summary>The checked-out branch, or null on a detached HEAD (as during a rebase).</summary>
    public string? CurrentBranch { get; }

    /// <summary>
    /// What is being brought in, for the user to recognise: the merge message,
    /// the branch being rebased, or the commit being picked or reverted.
    /// </summary>
    public string? Incoming { get; }

    public IReadOnlyList<GitConflictFile> Files { get; }

    /// <summary>The files someone still has to make a choice for.</summary>
    public IReadOnlyList<GitConflictFile> Open => Files.Where(f => f.NeedsResolving).ToList();

    /// <summary>
    /// The files edited free of conflict blocks that Git still lists as
    /// unmerged: they wait for the user to mark them as resolved.
    /// </summary>
    public IReadOnlyList<GitConflictFile> Resolved => Files.Where(f => !f.NeedsResolving).ToList();

    /// <summary>
    /// Equal for two readings with the same files in the same state and the
    /// same operation. A dismissed notice stays away until this changes.
    /// </summary>
    public string Signature =>
        Operation + "|" + Incoming + "|" + string.Join("\n",
            Files.Select(f => (f.NeedsResolving ? "open " : "done ") + f.Code + " " + f.Path).OrderBy(s => s, StringComparer.Ordinal));
}

/// <summary>
/// Reads the conflicts Git left in the working folder. Everything comes from
/// <c>git</c> on the PATH and from the files themselves. A folder that is not
/// a repository, or a missing git, reads as no conflicts. A git that does not
/// answer in time, or fails inside a repository, throws instead: that is not
/// a sign the conflicts are gone.
/// </summary>
public static class GitConflicts
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    // Larger files are not searched for blocks; they count as unresolved.
    private const long MaxScannedBytes = 8L * 1024 * 1024;

    private static readonly HashSet<string> UnmergedCodes = new(StringComparer.Ordinal)
    {
        "DD", "AU", "UD", "UA", "DU", "AA", "UU",
    };

    /// <summary>
    /// The repository root and the absolute Git folder of <paramref name="folder"/>,
    /// or null when it is not in a repository. In a linked worktree the Git
    /// folder is outside the root; it is the folder that holds the index.
    /// </summary>
    public static async Task<(string Root, string GitDir)?> FindRepositoryAsync(string folder, CancellationToken ct = default)
    {
        var output = await RunGitAsync(folder, ct, "rev-parse", "--show-toplevel", "--absolute-git-dir").ConfigureAwait(false);
        if (output is null) return null;

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 2) return null;
        return (ToWindowsPath(lines[0]), ToWindowsPath(lines[1]));
    }

    /// <summary>
    /// The conflicts in the repository that holds <paramref name="folder"/>,
    /// or null when it is not a repository or has no unmerged files.
    /// </summary>
    public static async Task<GitConflictState?> ReadAsync(string folder, CancellationToken ct = default)
    {
        var repo = await FindRepositoryAsync(folder, ct).ConfigureAwait(false);
        if (repo is null) return null;
        var (root, gitDir) = repo.Value;

        var status = await RunGitAsync(root, ct, "status", "--porcelain=v1", "-z", "--untracked-files=no").ConfigureAwait(false)
            ?? throw new InvalidOperationException("git status failed in " + root);

        var entries = ParseStatus(status);
        if (entries.Count == 0) return null;

        // Only both-modified and both-added files get blocks.
        var withBlocks = entries.Where(e => e.Code is "UU" or "AA").Select(e => e.Path).ToList();
        var markerSizes = withBlocks.Count == 0
            ? new Dictionary<string, int>()
            : await ReadMarkerSizesAsync(root, withBlocks, ct).ConfigureAwait(false);

        var files = new List<GitConflictFile>(entries.Count);
        foreach (var (code, path) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            var markers = code is "UU" or "AA"
                && FileHasMarkers(full, markerSizes.TryGetValue(path, out var size) ? size : DefaultMarkerSize);
            files.Add(new GitConflictFile(path, code, markers) { CanKeep = File.Exists(full) });
        }

        var operation = DetectOperation(gitDir);
        var incoming = await DescribeIncomingAsync(root, gitDir, operation, ct).ConfigureAwait(false);
        var branch = (await RunGitAsync(root, ct, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false))?.Trim();

        return new GitConflictState(root, operation, string.IsNullOrEmpty(branch) ? null : branch, incoming, files);
    }

    /// <summary>
    /// Marks files as resolved: <c>git add</c> for each, which is what accepting
    /// the merge of one file does. Only files that are still unmerged and free
    /// of conflict blocks at this moment are added, so a file that changed
    /// since the user looked is left alone. Returns the paths added, or null
    /// when git failed.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> MarkResolvedAsync(
        string folder, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var state = await ReadAsync(folder, ct).ConfigureAwait(false);
        if (state is null) return Array.Empty<string>();

        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        var ready = state.Resolved.Select(f => f.Path).Where(wanted.Contains).ToList();
        if (ready.Count == 0) return ready;

        var output = await RunGitAsync(state.RepositoryRoot, ct, new[] { "add", "--" }.Concat(ready).ToArray()).ConfigureAwait(false);
        return output is null ? null : ready;
    }

    /// <summary>
    /// Settles a conflict where only one side has the file: keeps the file as
    /// it is in the working folder (<c>git add</c>) or deletes it (<c>git rm</c>).
    /// Done only when the file is still in that conflict at this moment. Returns
    /// false when it is not, or when git failed.
    /// </summary>
    public static async Task<bool> SettleDeleteConflictAsync(
        string folder, string path, bool keep, CancellationToken ct = default)
    {
        var state = await ReadAsync(folder, ct).ConfigureAwait(false);
        var file = state?.Files.FirstOrDefault(f => f.Path == path && f.IsKeepOrDeleteConflict);
        if (state is null || file is null) return false;
        if (keep && !File.Exists(Path.Combine(state.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)))) return false;

        var args = keep ? new[] { "add", "--", path } : new[] { "rm", "-q", "--", path };
        return await RunGitAsync(state.RepositoryRoot, ct, args).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// The unmerged entries of <c>git status --porcelain=v1 -z</c>, as (code, path).
    /// A rename entry carries its old path as a second field; it is skipped.
    /// </summary>
    public static IReadOnlyList<(string Code, string Path)> ParseStatus(string output)
    {
        var result = new List<(string, string)>();
        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 4 || field[2] != ' ') continue;

            var code = field.Substring(0, 2);
            var path = field.Substring(3);

            // "R  new\0old": the old path is its own field.
            if (code[0] is 'R' or 'C' || code[1] is 'R' or 'C') i++;

            if (UnmergedCodes.Contains(code)) result.Add((code, path));
        }
        return result;
    }

    /// <summary>The kind of conflict as <c>git status</c> names it.</summary>
    public static string DescribeCode(string code) => code switch
    {
        "UU" => "both modified",
        "AA" => "both added",
        "DU" => "deleted by us",
        "UD" => "deleted by them",
        "AU" => "added by us",
        "UA" => "added by them",
        "DD" => "both deleted",
        _ => code,
    };

    /// <summary>Git's marker length when <c>conflict-marker-size</c> is not set for a file.</summary>
    public const int DefaultMarkerSize = 7;

    /// <summary>
    /// True when the text holds any line of a conflict block, not only a whole
    /// block: an agent that removed the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line
    /// and stopped leaves a file that is not resolved. A marker is exactly
    /// <paramref name="markerSize"/> '&lt;', '|' or '&gt;', then the end of the
    /// line or a space; or a line of exactly that many '=' alone.
    /// </summary>
    public static bool HasConflictMarkers(string text, int markerSize = DefaultMarkerSize)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (IsMarker(line, '<') || IsMarker(line, '|') || IsMarker(line, '>') || IsSeparator(line))
                return true;
        }
        return false;

        bool IsMarker(string line, char c) =>
            line.Length >= markerSize
            && RunOf(line, c) == markerSize
            && (line.Length == markerSize || line[markerSize] == ' ');

        bool IsSeparator(string line) =>
            line.TrimEnd().Length == markerSize && RunOf(line, '=') == markerSize;

        static int RunOf(string line, char c)
        {
            var n = 0;
            while (n < line.Length && line[n] == c) n++;
            return n;
        }
    }

    /// <summary>
    /// <c>conflict-marker-size</c> for each path that sets it in
    /// <c>.gitattributes</c>; paths without it are left out.
    /// </summary>
    private static async Task<Dictionary<string, int>> ReadMarkerSizesAsync(
        string root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = await RunGitAsync(root, ct,
            new[] { "check-attr", "-z", "conflict-marker-size", "--" }.Concat(paths).ToArray()).ConfigureAwait(false);
        if (output is null) return sizes;

        // "path\0attribute\0value\0" per path.
        var fields = output.Split('\0');
        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            if (int.TryParse(fields[i + 2], out var size) && size > 0)
                sizes[fields[i]] = size;
        }
        return sizes;
    }

    private static bool FileHasMarkers(string fullPath, int markerSize)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return false;
            // Cannot tell; Git does not hand a file this large to anyone to edit by hand anyway.
            if (info.Length > MaxScannedBytes) return true;

            var bytes = File.ReadAllBytes(fullPath);
            // A binary file gets no blocks; it stays unresolved until a side is chosen.
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) return true;

            return HasConflictMarkers(Encoding.UTF8.GetString(bytes), markerSize);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads the operation from the files Git keeps while it waits. A rebase
    /// that stops on a conflict also has REBASE_HEAD; the folder decides it.
    /// </summary>
    public static GitConflictOperation DetectOperation(string gitDir)
    {
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return GitConflictOperation.Rebase;
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return GitConflictOperation.Merge;
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return GitConflictOperation.CherryPick;
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return GitConflictOperation.Revert;
        return GitConflictOperation.None;
    }

    private static async Task<string?> DescribeIncomingAsync(string root, string gitDir, GitConflictOperation operation, CancellationToken ct)
    {
        switch (operation)
        {
            case GitConflictOperation.Merge:
                // "Merge branch 'feature/x'" names what comes in better than a hash.
                var message = FirstLine(Path.Combine(gitDir, "MERGE_MSG"));
                if (!string.IsNullOrWhiteSpace(message)) return message;
                return await DescribeCommitAsync(root, "MERGE_HEAD", ct).ConfigureAwait(false);

            case GitConflictOperation.Rebase:
                var headName = FirstLine(Path.Combine(gitDir, "rebase-merge", "head-name"))
                    ?? FirstLine(Path.Combine(gitDir, "rebase-apply", "head-name"));
                if (headName is not null && headName.StartsWith("refs/heads/", StringComparison.Ordinal))
                    headName = headName.Substring("refs/heads/".Length);
                var commit = await DescribeCommitAsync(root, "REBASE_HEAD", ct).ConfigureAwait(false);
                return headName is null ? commit : commit is null ? headName : $"{headName}, at {commit}";

            case GitConflictOperation.CherryPick:
                return await DescribeCommitAsync(root, "CHERRY_PICK_HEAD", ct).ConfigureAwait(false);

            case GitConflictOperation.Revert:
                return await DescribeCommitAsync(root, "REVERT_HEAD", ct).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private static async Task<string?> DescribeCommitAsync(string root, string reference, CancellationToken ct)
    {
        var line = (await RunGitAsync(root, ct, "log", "-1", "--format=%h %s", reference, "--").ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static string? FirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path, Encoding.UTF8);
            var line = reader.ReadLine()?.Trim();
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ToWindowsPath(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path.Replace('/', '\\') : path;

    /// <summary>
    /// Runs git and returns its output, or null when it cannot start or fails.
    /// Throws <see cref="TimeoutException"/> when it takes too long: the caller
    /// must not read that as "no conflicts". Prompts are off: there is no
    /// terminal to answer them. Optional locks are off as well, so reading the
    /// status does not rewrite the index, which the watcher would take for a
    /// change. Paths are literal, not patterns: <c>[</c>, <c>]</c>, <c>?</c>
    /// and <c>*</c> are legal in Windows file names (<c>pages/[id].tsx</c>),
    /// and as a pattern <c>git rm</c> would also delete <c>pages/i.tsx</c>.
    /// </summary>
    private static async Task<string?> RunGitAsync(string folder, CancellationToken ct, params string[] args)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", new[] { "-c", "core.quotepath=off" }.Concat(args).Select(Quote)),
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
        psi.EnvironmentVariables["NO_COLOR"] = "1";
        psi.EnvironmentVariables["GIT_LITERAL_PATHSPECS"] = "1";

        Process? process;
        try
        {
            process = Process.Start(psi);
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
            var exited = Task.Run(() => process.WaitForExit((int)GitTimeout.TotalMilliseconds), ct);

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
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"git {string.Join(" ", args.Take(2))} did not finish in {GitTimeout.TotalSeconds:0} seconds");
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
