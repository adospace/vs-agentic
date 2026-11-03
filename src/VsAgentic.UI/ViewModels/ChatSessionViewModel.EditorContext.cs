using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The file open in the editor, and the lines selected in it, offered beside
/// the @ and attach buttons. "Fix this" then needs no path pasted by hand.
///
/// Only offered: a file can be open by accident and a selection left over from
/// earlier, so nothing goes out until the user clicks the suggestion. The click
/// turns it into an attachment like a pasted file, removable with ×.
/// </summary>
public partial class ChatSessionViewModel
{
    // Beyond this the text is left out and only the line range goes: the CLI
    // can read the lines itself, and a whole file pasted into a prompt costs
    // context for no gain.
    private const int MaxSelectedLines = 500;
    private const int MaxSelectedChars = 30_000;

    private const string OpenInEditorPrefix = "Open in editor: `";
    private const string SelectedInEditorPrefix = "Selected in editor: `";

    private IEditorContextProvider? _editorContextProvider;

    /// <summary>
    /// Zero or one suggestion. A collection rather than a property so the
    /// button disappears with it, without a converter.
    /// </summary>
    public ObservableCollection<EditorContext> EditorContextSuggestions { get; } = new();

    /// <summary>Set by the host once, after construction. Null leaves the feature off.</summary>
    public IEditorContextProvider? EditorContextProvider
    {
        get => _editorContextProvider;
        set
        {
            if (_editorContextProvider is not null)
                _editorContextProvider.Changed -= RefreshEditorContext;

            _editorContextProvider = value;

            if (_editorContextProvider is not null)
                _editorContextProvider.Changed += RefreshEditorContext;

            RefreshEditorContext();
        }
    }

    /// <summary>
    /// Re-reads the editor. The host calls this when the mouse enters the chat
    /// and when the input box gets focus: a selection can only change while the
    /// user is in the editor, so coming back is when the suggestion has to
    /// catch up.
    /// </summary>
    public void RefreshEditorContext()
    {
        var context = ReadEditorContext();

        // Already attached: offering it again would only attach it twice.
        if (context is not null && IsAttached(context))
            context = null;

        var shown = EditorContextSuggestions.FirstOrDefault();
        if (shown?.Reference == context?.Reference && shown?.SelectedText == context?.SelectedText)
            return;

        EditorContextSuggestions.Clear();
        if (context is not null)
            EditorContextSuggestions.Add(context);
    }

    /// <summary>
    /// Attaches what the editor holds at the click. The button may still show
    /// an older state — a selection made after the last refresh — and the
    /// user clicked for what is selected now, not for what was.
    /// </summary>
    public void AcceptEditorContext(EditorContext suggestion)
    {
        Attach(ReadEditorContext() ?? suggestion);
    }

    private bool IsAttached(EditorContext context) =>
        PendingAttachments.Any(a => string.Equals(a.SourcePath, context.Reference, StringComparison.OrdinalIgnoreCase));

    private EditorContext? ReadEditorContext()
    {
        try
        {
            return _editorContextProvider?.GetCurrent();
        }
        catch
        {
            // The editor is a convenience; a failure to read it must not get
            // in the way of the chat.
            return null;
        }
    }

    /// <summary>
    /// Puts the attached editor contexts above the user's text, one line each.
    /// <paramref name="shown"/> is what the transcript keeps: the paths in
    /// backticks, which the transcript turns into links to the lines.
    /// <paramref name="sent"/> also carries the selected text, which may not be
    /// saved yet, so reading the file would give the model something else.
    /// </summary>
    private static void WithEditorContexts(
        IReadOnlyList<EditorContext> contexts, ref string shown, out string sent)
    {
        if (contexts.Count == 0)
        {
            sent = shown;
            return;
        }

        var shownHead = new StringBuilder();
        var sentHead = new StringBuilder();
        foreach (var context in contexts)
        {
            var line = context.HasSelection
                ? $"{SelectedInEditorPrefix}{context.Reference}`"
                : $"{OpenInEditorPrefix}{context.Reference}`";

            shownHead.Append(line).Append('\n');
            sentHead.Append(line).Append('\n');
            if (SelectionBlock(context) is { } block)
                sentHead.Append(block).Append('\n');
        }

        var text = shown;
        shown = Join(shownHead.ToString().TrimEnd('\n'), text);
        sent = Join(sentHead.ToString().TrimEnd('\n'), text);

        static string Join(string head, string text) =>
            string.IsNullOrEmpty(text) ? head : $"{head}\n\n{text}";
    }

    /// <summary>
    /// The message without its editor lines. The session title is about what
    /// the user asked, and a message that is only editor lines asked nothing.
    /// </summary>
    private static string WithoutEditorContextLines(string message)
    {
        while (message.StartsWith(OpenInEditorPrefix, StringComparison.Ordinal) ||
               message.StartsWith(SelectedInEditorPrefix, StringComparison.Ordinal))
        {
            var end = message.IndexOf('\n');
            message = end < 0 ? "" : message.Substring(end + 1).TrimStart('\r', '\n');
        }

        return message;
    }

    private static string? SelectionBlock(EditorContext context)
    {
        if (!context.HasSelection || string.IsNullOrEmpty(context.SelectedText))
            return null;

        var text = context.SelectedText!;
        var lineCount = context.EndLine!.Value - context.StartLine!.Value + 1;
        if (lineCount > MaxSelectedLines || text.Length > MaxSelectedChars)
            return "The selection is too long to include; read those lines from the file.";

        // A fence longer than any run of backticks in the code, so code that
        // contains a fence of its own cannot close this one.
        var longestRun = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }
        var fence = new string('`', Math.Max(3, longestRun + 1));

        return new StringBuilder()
            .Append("Selected text:\n")
            .Append(fence).Append('\n')
            .Append(text.TrimEnd('\r', '\n')).Append('\n')
            .Append(fence)
            .ToString();
    }
}
