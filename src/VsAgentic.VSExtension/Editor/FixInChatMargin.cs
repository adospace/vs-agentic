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

    // Every document view gets the margin, also one whose buffer has no file
    // yet: a new file saved later gets its path then, and the margin reads
    // the path each time it draws. Without a path it takes no width.
    public IWpfTextViewMargin? CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        => new FixInChatMargin(wpfTextViewHost.TextView, TextDocumentFactory);
}

internal sealed class FixInChatMargin : Canvas, IWpfTextViewMargin
{
    public const string MarginName = "VsAgentic Fix in Chat";

    private const double IconSize = 14;
    private const double MarginWidth = 16;

    private readonly IWpfTextView _view;
    private readonly ITextDocumentFactoryService _documents;
    private ITextDocument? _document;
    private bool _disposed;

    public FixInChatMargin(IWpfTextView view, ITextDocumentFactoryService documents)
    {
        _view = view;
        _documents = documents;

        Width = 0;
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

    /// <summary>
    /// The file behind the view, or null for a buffer with none (a diff, a
    /// peek, a new file not yet saved). Looked up again until there is one,
    /// and read each time: Save As changes the path.
    /// </summary>
    private string? FilePath()
    {
        if (_document is null &&
            _documents.TryGetTextDocument(_view.TextDataModel.DocumentBuffer, out var document))
        {
            _document = document;
        }
        return _document?.FilePath is { Length: > 0 } path && System.IO.Path.IsPathRooted(path) ? path : null;
    }

    private void Redraw()
    {
        if (_disposed || _view.IsClosed || _view.InLayout) return;

        Children.Clear();

        // No file, no Error List entries: the margin takes no room.
        if (FilePath() is not { } path)
        {
            Width = 0;
            return;
        }

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
                var problems = ErrorListProblems.OnLines(path, number, number);
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
