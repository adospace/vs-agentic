using System.Text;

namespace VsAgentic.Services.Services;

/// <summary>
/// The message that asks the agent to resolve the conflicts. It holds only
/// the state and the rules; the agent reads the sides and the history with
/// git itself, so any agent that can run git can follow it.
/// </summary>
public static class ConflictResolutionRequest
{
    /// <summary>The message for <paramref name="state"/>, with the user's own words after it.</summary>
    public static string Build(GitConflictState state, string? userNote = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Resolve the Git conflicts in this repository.");
        sb.AppendLine();
        sb.AppendLine(DescribeOperation(state));
        sb.AppendLine();
        sb.AppendLine("Files with conflicts:");
        foreach (var file in state.Open)
            sb.AppendLine($"- `{file.Path}` ({file.Kind})");

        sb.AppendLine();
        sb.AppendLine("For each file:");
        sb.AppendLine("1. Find out what each side changed and why. Read the versions with `git show :1:<path>` (common base), `:2:<path>` (ours) and `:3:<path>` (theirs), and the commits behind them, for example `git log --merge --oneline -- <path>` and `git log -p` of the commits that touch the conflicting lines.");
        sb.AppendLine("2. Edit the file in the working folder so that it keeps what both sides meant, and remove every conflict marker. Keep the rest of the file as it is.");
        sb.AppendLine("3. When only one side has the file (deleted by us or them, added by us or them), do not edit or delete it. Recommend keeping it or deleting it, and say why.");
        sb.AppendLine();
        sb.AppendLine("Do not stage or finish anything unless I ask: no `git add`, `git rm`, `git commit`, `git reset`, `git stash`, and no `--continue`, `--skip` or `--abort`. Do not replace a file with one side either: no `git checkout --ours`/`--theirs`, `git checkout -- <path>` or `git restore`, which throw away the other side and my uncommitted edits. I review your changes and mark the files as resolved myself, in Visual Studio; do not give me git commands for that.");
        sb.AppendLine();
        sb.AppendLine("When you are done, write one short section per file: what each side changed, what you kept and why. Mark each file where you are not sure. Then list the files you did not resolve.");

        if (!string.IsNullOrWhiteSpace(userNote))
        {
            sb.AppendLine();
            sb.Append("Also: ").AppendLine(userNote!.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>One sentence on what stopped on the conflicts.</summary>
    public static string DescribeOperation(GitConflictState state)
    {
        var into = state.CurrentBranch is null ? "" : $" into `{state.CurrentBranch}`";
        var incoming = state.Incoming is null ? null : $"`{state.Incoming}`";

        return state.Operation switch
        {
            GitConflictOperation.Merge => incoming is null
                ? $"A merge{into} stopped on conflicts."
                : $"A merge{into} stopped on conflicts. Merge message: {incoming}.",
            GitConflictOperation.Rebase => incoming is null
                ? "A rebase stopped on conflicts. In a rebase, \"ours\" (:2) is the branch being rebased onto, and \"theirs\" (:3) is the commit being applied."
                : $"A rebase of {incoming} stopped on conflicts. In a rebase, \"ours\" (:2) is the branch being rebased onto, and \"theirs\" (:3) is the commit being applied.",
            GitConflictOperation.CherryPick => incoming is null
                ? $"A cherry-pick{into} stopped on conflicts."
                : $"A cherry-pick of {incoming}{into} stopped on conflicts.",
            GitConflictOperation.Revert => incoming is null
                ? $"A revert{into} stopped on conflicts."
                : $"A revert of {incoming}{into} stopped on conflicts.",
            _ => "No merge, rebase, cherry-pick or revert is in progress (the conflicts may come from `git stash pop` or a checkout with `-m`).",
        };
    }
}
