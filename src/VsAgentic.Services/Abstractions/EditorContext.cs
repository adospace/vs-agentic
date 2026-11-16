using System.IO;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// The document the user has open in the editor, and the lines selected in it.
/// Offered above the input box as a suggestion; once the user clicks it, it is
/// an attachment like a pasted file. Line numbers are 1-based and inclusive;
/// both are null when nothing is selected.
/// </summary>
public sealed class EditorContext : IChatAttachment
{
    public string FilePath { get; }
    public int? StartLine { get; }
    public int? EndLine { get; }

    /// <summary>The selected text as the editor holds it, saved or not.</summary>
    public string? SelectedText { get; }

    public bool HasSelection => StartLine is not null;

    /// <summary>File name, plus the line range when there is a selection.</summary>
    public string DisplayName { get; }

    public string FileName { get; }

    /// <summary>" · lines 12–30", or empty without a selection. Kept apart from
    /// the name so a long name can be shortened without losing the lines.</summary>
    public string LineRange { get; }

    /// <summary>
    /// The path in the form the transcript links: <c>path</c> or
    /// <c>path:start-end</c>. The same file and lines attached twice are one
    /// attachment, and the whole file attached by hand is the same as this.
    /// </summary>
    public string Reference { get; }

    public string? SourcePath => Reference;

    public EditorContext(string filePath, int? startLine = null, int? endLine = null, string? selectedText = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("Path is required.", nameof(filePath))
            : filePath;

        if (startLine is not null && endLine is not null)
        {
            StartLine = startLine;
            EndLine = endLine;
            SelectedText = selectedText;
        }

        FileName = Path.GetFileName(filePath);
        LineRange = StartLine is null ? ""
            : StartLine == EndLine ? $" · line {StartLine}"
            : $" · lines {StartLine}–{EndLine}";
        DisplayName = FileName + LineRange;

        Reference = StartLine is null ? filePath
            : StartLine == EndLine ? $"{filePath}:{StartLine}"
            : $"{filePath}:{StartLine}-{EndLine}";
    }
}

/// <summary>
/// Reads what the user has open in the editor. Implemented by the Visual Studio
/// host; the chat only sees the result. Called on the UI thread.
/// </summary>
public interface IEditorContextProvider
{
    /// <summary>
    /// The active text document and its selection, or null when no text
    /// document is open or the user has turned the suggestion off.
    /// </summary>
    EditorContext? GetCurrent();

    /// <summary>Raised on the UI thread when another document becomes active.</summary>
    event Action? Changed;
}
