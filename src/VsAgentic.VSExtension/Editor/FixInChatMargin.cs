using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VsAgentic.VSExtension.IdeContext;

namespace VsAgentic.VSExtension.Editor;

/// <summary>
/// A narrow margin of its own, left of the breakpoint margin, with the
/// "Fix in VsAgentic Chat" icon on each line the Error List has an error or a
/// warning on. In the glyph margin a breakpoint covered the icon and the
/// click set a breakpoint as well; here neither happens.
/// </summary>
[Export(typeof(IWpfTextViewMarginProvider))]
[Name(FixInChatMargin.MarginName)]
[MarginContainer(PredefinedMarginNames.Left)]
[Order(Before = PredefinedMarginNames.Glyph)]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class FixInChatMarginProvider : IWpfTextViewMarginProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocumentFactory { get; set; } = null!;

    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
    {
        // A buffer with no file (a diff, a peek, a new file not yet saved)
        // has no Error List entries.
        if (!TextDocumentFactory.TryGetTextDocument(wpfTextViewHost.TextView.TextDataModel.DocumentBuffer, out var document))
            return null;

        return new FixInChatMargin(wpfTextViewHost.TextView, document);
    }
}

internal sealed class FixInChatMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "VsAgentic Fix in Chat";

    private const double IconSize = 14;
    private const double MarginWidth = 16;

    private readonly IWpfTextView _view;
    private readonly ITextDocument _document;
    private bool _disposed;

    public FixInChatMargin(IWpfTextView view, ITextDocument document)
    {
        _view = view;
        _document = document;

        Width = MarginWidth;
        ClipToBounds = true;
        // Transparent rather than null, so the margin keeps its mouse events
        // between icons too and a click there does nothing.
        Background = System.Windows.Media.Brushes.Transparent;

        _view.LayoutChanged += OnLayoutChanged;
        _view.ZoomLevelChanged += OnZoomLevelChanged;
        ErrorListProblems.Changed += OnProblemsChanged;
    }

    private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e) => Redraw();

    public FrameworkElement VisualElement => this;
    public double MarginSize => ActualWidth;
    public bool Enabled => true;

    public ITextViewMargin? GetTextViewMargin(string marginName)
        => string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Redraw();

    // From a background thread, after the Error List has settled.
    private void OnProblemsChanged() => VsAgenticPackage.Start(async () =>
    {
        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Redraw();
    });

    private void Redraw()
    {
        if (_disposed || _view.IsClosed || _view.InLayout) return;

        Children.Clear();

        // The editor zooms the text but not this margin, and the lines'
        // positions are in the unzoomed space of the text. The margin scales
        // them itself, as the breakpoint and line number margins do.
        var zoom = _view.ZoomLevel / 100.0;
        var width = MarginWidth * zoom;
        var size = IconSize * zoom;
        Width = width;

        try
        {
            foreach (var line in _view.TextViewLines)
            {
                // A wrapped line shows the icon once, on its first row.
                if (!line.IsFirstTextViewLineForSnapshotLine) continue;

                var number = line.Start.GetContainingLine().LineNumber;
                var problems = ErrorListProblems.OnLines(_document.FilePath, number, number);
                if (problems.Count == 0) continue;

                var icon = FixInChat.CreateIcon(size);
                icon.Cursor = Cursors.Hand;
                icon.ToolTip = FixInChat.Tooltip(problems);
                icon.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    VsAgenticPackage.StartFixInChat(problems);
                };

                SetLeft(icon, (width - size) / 2);
                SetTop(icon, (line.TextTop - _view.ViewportTop + line.TextHeight / 2) * zoom - size / 2);
                Children.Add(icon);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The view changed under us; the next layout draws again.
            Serilog.Log.Debug(ex, "[FixInChat] Could not draw the margin");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.LayoutChanged -= OnLayoutChanged;
        _view.ZoomLevelChanged -= OnZoomLevelChanged;
        ErrorListProblems.Changed -= OnProblemsChanged;
    }
}
