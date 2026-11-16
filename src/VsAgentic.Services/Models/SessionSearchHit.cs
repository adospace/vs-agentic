namespace VsAgentic.Services.Models;

/// <summary>
/// The first place in a session's conversation where a search term occurs,
/// cut down to one line of context around it. The three parts are kept apart
/// so the list can set the match in bold.
/// </summary>
public class SessionSearchHit
{
    public int SessionId { get; set; }

    /// <summary>Text before the match, with a leading ellipsis when cut.</summary>
    public string Before { get; set; } = "";

    /// <summary>The matched text as written in the message.</summary>
    public string Match { get; set; } = "";

    /// <summary>Text after the match, with a trailing ellipsis when cut.</summary>
    public string After { get; set; } = "";
}
