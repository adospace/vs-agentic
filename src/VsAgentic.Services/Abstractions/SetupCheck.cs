using System;
using System.Collections.Generic;

namespace VsAgentic.Services.Abstractions;

/// <summary>How one line of the setup check came out.</summary>
public enum SetupCheckStatus
{
    /// <summary>Works.</summary>
    Ok,

    /// <summary>Works, but something may get in the way.</summary>
    Warning,

    /// <summary>Does not work; the chat cannot run until it is fixed.</summary>
    Error,

    /// <summary>Not a check: a fact worth having in a bug report.</summary>
    Info,
}

/// <summary>
/// One line of the setup check. <see cref="Detail"/> says what was found and,
/// when something is wrong, what to do about it, in one or two sentences.
/// It may hold Markdown code spans (paths, commands).
/// <see cref="Fix"/> is set when VsAgentic can fix the problem on a click.
/// </summary>
public sealed record SetupCheckItem(SetupCheckStatus Status, string Title, string Detail, SetupFix? Fix = null);

/// <summary>What a setup fix does.</summary>
public enum SetupFixKind
{
    /// <summary>Runs Anthropic's installer for Claude Code in a terminal window.</summary>
    InstallCli,

    /// <summary>Runs <c>claude update</c> in a terminal window.</summary>
    UpdateCli,

    /// <summary>Runs <c>claude auth login</c> in a terminal window.</summary>
    SignIn,

    /// <summary>Sets Claude CLI Path to a <c>claude.exe</c> found on disk.</summary>
    UseFoundCli,

    /// <summary>Opens the download page of the WebView2 runtime.</summary>
    InstallWebView2,
}

/// <summary>
/// A fix the setup check offers for one of its lines: a button with
/// <see cref="Label"/>. <see cref="Path"/> is the <c>claude.exe</c> for
/// <see cref="SetupFixKind.UseFoundCli"/>, and the CLI to run for
/// <see cref="SetupFixKind.UpdateCli"/> and <see cref="SetupFixKind.SignIn"/>.
/// </summary>
public sealed record SetupFix(SetupFixKind Kind, string Label, string? Path = null)
{
    /// <summary>Names the fix in a message from the chat page.</summary>
    public string Id => Kind.ToString();
}

/// <summary>
/// A chat service that can say how its own session is doing: what the agent
/// behind it reported when it started, and whether its tools connected. The
/// setup check adds these lines when it runs inside a chat.
///
/// Separate from <see cref="IChatService"/> so a service that has nothing to
/// report does not have to implement it.
/// </summary>
public interface ISessionSetupInfo
{
    /// <summary>The lines for this session. Must not block or throw.</summary>
    IReadOnlyList<SetupCheckItem> DescribeSession();

    /// <summary>
    /// Raised when a message could not start the CLI because the executable
    /// was not found. The chat then offers the setup check on its own.
    /// </summary>
    event Action? CliNotFound;

    /// <summary>
    /// Stops the CLI process after a setup fix, so the next message starts
    /// one with the new path or sign-in. Does nothing while a turn runs.
    /// </summary>
    void RestartAfterSetupFix();
}
