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
/// </summary>
public sealed record SetupCheckItem(SetupCheckStatus Status, string Title, string Detail);

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
}
