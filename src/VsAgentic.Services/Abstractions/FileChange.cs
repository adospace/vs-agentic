using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// What an Edit, MultiEdit or Write call did to a file, as the CLI reports it
/// once the call has run. Saved with the step, but only the hunks: the whole
/// file before and after can run to megabytes per edit, so a reopened session
/// shows the changed parts only.
/// </summary>
public sealed class FileChange
{
    public string FilePath { get; set; } = "";

    /// <summary>True when the call created the file.</summary>
    public bool IsNewFile { get; set; }

    public List<FileChangeHunk> Hunks { get; set; } = new();

    /// <summary>
    /// The whole file before and after the call, with <c>\n</c> line endings.
    /// Only known in the session that made the change; null after a restore,
    /// or when the hunks did not apply to the file the CLI sent.
    /// </summary>
    [JsonIgnore] public string? Before { get; set; }
    [JsonIgnore] public string? After { get; set; }
}

/// <summary>
/// One hunk of a unified diff, as in the CLI's <c>structuredPatch</c>. Each
/// line starts with <c>' '</c> (context), <c>'-'</c> (removed) or <c>'+'</c>
/// (added).
/// </summary>
public sealed class FileChangeHunk
{
    public int OldStart { get; set; }
    public int OldLines { get; set; }
    public int NewStart { get; set; }
    public int NewLines { get; set; }
    public List<string> Lines { get; set; } = new();
}

/// <summary>
/// The two sides of a comparison the host opens in its diff viewer.
/// </summary>
public sealed class FileDiffContent
{
    public required string FilePath { get; init; }
    public required string Before { get; init; }
    public required string After { get; init; }
    public required string BeforeLabel { get; init; }
    public required string AfterLabel { get; init; }

    /// <summary>
    /// True when both sides hold only the changed parts of the file, not the
    /// whole file.
    /// </summary>
    public bool ChangedPartsOnly { get; init; }
}
