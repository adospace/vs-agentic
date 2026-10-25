namespace VsAgentic.Services.IdeContext;

/// <summary>
/// A short, current account of one part of the IDE's state — the Error List,
/// the editor, the debugger. Every provider reports in this one shape, so the
/// chat can show any of them the same way and Claude can tell how fresh each
/// one is. The details stay behind the provider's tools.
/// </summary>
public sealed class IdeContextSection
{
    /// <summary>What the section describes, e.g. "diagnostics" or "editor".</summary>
    public string Kind { get; }

    /// <summary>One or a few lines, small enough to send with every message.</summary>
    public string Summary { get; }

    /// <summary>True when a tool of the same provider returns more than the summary.</summary>
    public bool DetailsAvailable { get; }

    public DateTimeOffset CapturedAt { get; }

    /// <summary>
    /// True when the IDE may have changed since the capture, e.g. a build or
    /// background analysis was still running.
    /// </summary>
    public bool IsStale { get; }

    /// <summary>Where the data comes from, e.g. "Error List".</summary>
    public string Source { get; }

    /// <summary>
    /// Changes whenever the underlying data changes, so two captures can be
    /// compared without comparing their content. Null when the source has none.
    /// </summary>
    public string? Version { get; }

    public IdeContextSection(
        string kind,
        string summary,
        string source,
        DateTimeOffset capturedAt,
        bool detailsAvailable = false,
        bool isStale = false,
        string? version = null)
    {
        Kind = kind;
        Summary = summary;
        Source = source;
        CapturedAt = capturedAt;
        DetailsAvailable = detailsAvailable;
        IsStale = isStale;
        Version = version;
    }
}
