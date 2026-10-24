using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VsAgentic.Services.Configuration;

/// <summary>
/// A slash command or skill the user can run from the chat input, as the
/// CLI reports it. <see cref="Name"/> is what follows the slash.
/// </summary>
public sealed record ClaudeCommandInfo(string Name, string? Description, string? ArgumentHint, bool IsBuiltin);

/// <summary>
/// The commands the CLI offered in its last report, from any session: the
/// answer to <c>initialize</c>, narrowed by <c>system/init</c>, and replaced
/// by <c>commands_changed</c>. Shared like <see cref="ClaudeModelCatalog.Reported"/>:
/// the list depends on the account, the CLI and the installed skills, not on
/// the conversation, and a session that has not sent anything yet has no
/// process to ask.
/// </summary>
public static class ClaudeCommandCatalog
{
    private static IReadOnlyList<ClaudeCommandInfo>? s_reported;

    /// <summary>Null until a CLI has answered.</summary>
    public static IReadOnlyList<ClaudeCommandInfo>? Reported => Volatile.Read(ref s_reported);

    /// <summary>Raised on a background thread when <see cref="Reported"/> changes.</summary>
    public static event Action<IReadOnlyList<ClaudeCommandInfo>>? ReportedChanged;

    public static void SetReported(IReadOnlyList<ClaudeCommandInfo> commands)
    {
        // Kept, not replaced, when equal: sessions compare the reference, and
        // every turn reports the list again.
        var previous = Volatile.Read(ref s_reported);
        if (previous is not null && previous.SequenceEqual(commands)) return;

        Volatile.Write(ref s_reported, commands);
        ReportedChanged?.Invoke(commands);
    }
}
