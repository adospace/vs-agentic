namespace VsAgentic.Services.Models;

public class SessionEntry
{
    public int Id { get; set; }
    public string Title { get; set; } = "New Session";

    /// <summary>
    /// True once the user has renamed the session by hand. The title generated
    /// from the first message is then skipped, so a manual name is never
    /// overwritten.
    /// </summary>
    public bool TitleIsCustom { get; set; }

    /// <summary>
    /// Pinned sessions are listed above the rest, regardless of activity.
    /// </summary>
    public bool IsPinned { get; set; }

    public int Ordinal { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }

    /// <summary>
    /// When the session was archived, or null while it is a normal session.
    /// Archiving only sets this field; the session folder stays where it is,
    /// so no stored path changes. An index written by an older build has no
    /// such field, which deserializes to null and so reads as not archived.
    /// </summary>
    public DateTime? ArchivedUtc { get; set; }
}
