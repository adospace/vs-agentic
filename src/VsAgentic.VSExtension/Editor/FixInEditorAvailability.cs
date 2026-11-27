using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VsAgentic.VSExtension.Commands;
using VsAgentic.VSExtension.IdeContext;

namespace VsAgentic.VSExtension.Editor;

/// <summary>
/// Keeps "Fix in VsAgentic Chat" in the editor's context menu enabled only
/// while the caret's line, or a selected line, has an error or a warning.
/// Follows the document view the user is in: the caret, the selection, and
/// the Error List changing under a caret that stands still.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class FixInEditorAvailability : IWpfTextViewCreationListener
{
    private static ITextView? _focused;
    private static ITextDocumentFactoryService? _documents;

    [Import]
    internal ITextDocumentFactoryService TextDocumentFactory { get; set; } = null!;

    public void TextViewCreated(IWpfTextView textView)
    {
        _documents ??= TextDocumentFactory;

        void Update(object? sender, EventArgs e) => Refresh(textView);
        void OnFocus(object? sender, EventArgs e)
        {
            _focused = textView;
            Refresh(textView);
        }

        textView.GotAggregateFocus += OnFocus;
        textView.Caret.PositionChanged += Update;
        textView.Selection.SelectionChanged += Update;
        textView.Closed += (_, _) =>
        {
            textView.GotAggregateFocus -= OnFocus;
            textView.Caret.PositionChanged -= Update;
            textView.Selection.SelectionChanged -= Update;
            if (_focused == textView) _focused = null;
        };

        if (textView.HasAggregateFocus) OnFocus(null, EventArgs.Empty);
    }

    static FixInEditorAvailability()
    {
        ErrorListProblems.Changed += () =>
        {
            if (_focused is { } view)
                VsAgenticPackage.Start(async () =>
                {
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!view.IsClosed) Refresh(view);
                });
        };
    }

    private static void Refresh(ITextView view)
    {
        if (view != _focused && !view.HasAggregateFocus) return;

        try
        {
            if (_documents is null || !_documents.TryGetTextDocument(view.TextBuffer, out var document))
            {
                FixInEditorCommand.SetAvailable(false);
                return;
            }

            var snapshot = view.TextSnapshot;
            var (first, last) = Lines(view, snapshot);
            FixInEditorCommand.SetAvailable(ErrorListProblems.OnLines(document.FilePath, first, last).Count > 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Debug(ex, "[FixInChat] Could not update the editor command");
        }
    }

    /// <summary>The 0-based lines of the selection, or the caret's line without one.</summary>
    internal static (int First, int Last) Lines(ITextView view, ITextSnapshot snapshot)
    {
        if (view.Selection.IsEmpty)
        {
            var line = view.Caret.Position.BufferPosition.GetContainingLine().LineNumber;
            return (line, line);
        }

        var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
        var first = snapshot.GetLineNumberFromPosition(span.Start);
        var last = snapshot.GetLineNumberFromPosition(span.End);

        // Whole lines selected end at the start of the next line, which is
        // not part of what the user meant.
        if (last > first && span.End == snapshot.GetLineFromLineNumber(last).Start)
            last--;

        return (first, last);
    }
}
