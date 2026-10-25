using System;
using System.Collections.Generic;
using System.Linq;

namespace VsAgentic.Services.Configuration;

/// <summary>
/// A slash command or skill the user can run from the chat input, as the
/// CLI reports it. <see cref="Name"/> is what follows the slash.
/// </summary>
public sealed record ClaudeCommandInfo(string Name, string? Description, string? ArgumentHint, bool IsBuiltin);

/// <summary>
/// The commands the CLI offered in its last report: the answer to
/// <c>initialize</c>, narrowed by <c>system/init</c>, and replaced by
/// <c>commands_changed</c>. One list per working directory, because the
/// project's own skills come from that folder. Within a folder the list is
/// shared like <see cref="ClaudeModelCatalog.Reported"/>: the rest depends on
/// the account, the CLI and the installed skills, not on the conversation,
/// and a session that has not sent anything yet has no process to ask.
/// </summary>
public static class ClaudeCommandCatalog
{
    private static readonly object s_lock = new object();
    private static readonly Dictionary<string, IReadOnlyList<ClaudeCommandInfo>> s_reported =
        new Dictionary<string, IReadOnlyList<ClaudeCommandInfo>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list for a working directory. Null until a CLI started there has answered.</summary>
    public static IReadOnlyList<ClaudeCommandInfo>? Reported(string workingDirectory)
    {
        lock (s_lock)
            return s_reported.TryGetValue(Key(workingDirectory), out var commands) ? commands : null;
    }

    /// <summary>
    /// Raised on a background thread with the working directory whose list
    /// changed. Two reports can race, so a handler reads <see cref="Reported"/>
    /// rather than keeping a list of its own from the event.
    /// </summary>
    public static event Action<string>? ReportedChanged;

    public static void SetReported(string workingDirectory, IReadOnlyList<ClaudeCommandInfo> commands)
    {
        var key = Key(workingDirectory);
        lock (s_lock)
        {
            // Kept, not replaced, when equal: every turn reports the list again.
            if (s_reported.TryGetValue(key, out var previous) && previous.SequenceEqual(commands)) return;
            s_reported[key] = commands;
        }
        ReportedChanged?.Invoke(key);
    }

    private static string Key(string workingDirectory)
    {
        try { return System.IO.Path.GetFullPath(workingDirectory).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return workingDirectory;
        }
    }

    /// <summary>Whether two working directories name the same catalog entry.</summary>
    public static bool SameDirectory(string a, string b) =>
        string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
}
