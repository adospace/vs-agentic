using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.VSExtension.Editor;

/// <summary>
/// Reads the active text document and its selection for the chat windows.
///
/// The active document is the shell's document frame, not the active window:
/// while the user types in the chat, the chat is the active window, but the
/// document frame stays on the file they came from — also when the chat is
/// docked among the documents.
/// </summary>
internal sealed class EditorContextTracker : IEditorContextProvider, IVsSelectionEvents, IDisposable
{
    private readonly IVsMonitorSelection _monitorSelection;
    private readonly Func<bool> _isEnabled;
    private uint _cookie;

    public event Action? Changed;

    public EditorContextTracker(IVsMonitorSelection monitorSelection, Func<bool> isEnabled)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _monitorSelection = monitorSelection;
        _isEnabled = isEnabled;
        _monitorSelection.AdviseSelectionEvents(this, out _cookie);
    }

    public EditorContext? GetCurrent()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return _isEnabled() ? ReadActive() : null;
    }

    /// <summary>
    /// The same, regardless of the option. The option turns off the suggestion
    /// in the chat; a command the user picks from the editor asks explicitly.
    /// </summary>
    public EditorContext? ReadActive()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!TryGetActiveView(out var path, out var view))
            return null;

        if (ErrorHandler.Failed(view.GetSelection(out var anchorLine, out var anchorColumn, out var endLine, out var endColumn)) ||
            (anchorLine == endLine && anchorColumn == endColumn))
        {
            return new EditorContext(path);
        }

        // The selection runs either way, depending on where the drag started.
        var anchorFirst = anchorLine < endLine || (anchorLine == endLine && anchorColumn < endColumn);
        var firstLine = anchorFirst ? anchorLine : endLine;
        var lastLine = anchorFirst ? endLine : anchorLine;
        var lastColumn = anchorFirst ? endColumn : anchorColumn;

        // Whole lines selected with the keyboard end at the start of the next
        // line; that line is not part of what the user meant.
        if (lastLine > firstLine && lastColumn == 0)
            lastLine--;

        if (ErrorHandler.Failed(view.GetSelectedText(out var text)) || string.IsNullOrWhiteSpace(text))
            return new EditorContext(path);

        return new EditorContext(path, firstLine + 1, lastLine + 1, text);
    }

    /// <summary>
    /// The active text document and the caret in it, 0-based; regardless of
    /// the option, like <see cref="ReadActive"/>.
    /// </summary>
    public (string Path, int Line, int Column)? ReadCaret()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!TryGetActiveView(out var path, out var view) ||
            ErrorHandler.Failed(view.GetCaretPos(out var line, out var column)))
        {
            return null;
        }

        return (path, line, column);
    }

    private bool TryGetActiveView(out string path, out IVsTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        path = null!;
        view = null!;

        if (ErrorHandler.Failed(_monitorSelection.GetCurrentElementValue(
                (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var value)) ||
            value is not IVsWindowFrame frame)
        {
            return false;
        }

        // A new file that was never saved has a moniker but no path the CLI
        // could open.
        if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var moniker)) ||
            moniker is not string documentPath ||
            !Path.IsPathRooted(documentPath))
        {
            return false;
        }

        // Designers and other non-text documents have no text view; a file
        // shown only as a form says nothing useful as a path on its own.
        if (VsShellUtilities.GetTextView(frame) is not { } textView)
            return false;

        path = documentPath;
        view = textView;
        return true;
    }

    public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
    {
        if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame)
            Changed?.Invoke();

        return VSConstants.S_OK;
    }

    public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
        IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew) => VSConstants.S_OK;

    public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_cookie != 0)
        {
            _monitorSelection.UnadviseSelectionEvents(_cookie);
            _cookie = 0;
        }
    }
}
