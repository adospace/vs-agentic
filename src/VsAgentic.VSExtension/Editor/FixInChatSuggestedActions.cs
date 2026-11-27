using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VsAgentic.Services.IdeContext;
using VsAgentic.VSExtension.IdeContext;

namespace VsAgentic.VSExtension.Editor;

/// <summary>
/// "Fix in VsAgentic Chat" in the light bulb on a line with an error or a
/// warning. The problems come from the Error List, the same entries Claude
/// reads with get_diagnostics, so the light bulb offers exactly what the list
/// shows for that line.
/// </summary>
[Export(typeof(ISuggestedActionsSourceProvider))]
[Name("VsAgentic Fix in Chat")]
[ContentType("text")]
internal sealed class FixInChatSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocumentFactory { get; set; } = null!;

    public ISuggestedActionsSource? CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer)
        => textView is null || textBuffer is null ? null : new FixInChatSuggestedActionsSource(TextDocumentFactory, textBuffer);
}

internal sealed class FixInChatSuggestedActionsSource : ISuggestedActionsSource
{
    private readonly ITextDocumentFactoryService _documents;
    private readonly ITextBuffer _buffer;

    public FixInChatSuggestedActionsSource(ITextDocumentFactoryService documents, ITextBuffer buffer)
    {
        _documents = documents;
        _buffer = buffer;
        ErrorListProblems.Changed += OnProblemsChanged;
    }

    // A build that ends while the caret rests on a line brings the action
    // there without the caret moving.
    public event EventHandler<EventArgs>? SuggestedActionsChanged;

    private void OnProblemsChanged() => SuggestedActionsChanged?.Invoke(this, EventArgs.Empty);

    // The requested categories are not checked. Visual Studio 2026 asks
    // HasSuggestedActionsAsync for CodeFix and Refactoring, but then asks
    // GetSuggestedActions for Any, so a test for CodeFix there would turn
    // the action down after offering it.
    public Task<bool> HasSuggestedActionsAsync(
        ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
        => Task.FromResult(ProblemsIn(range).Count > 0);

    public IEnumerable<SuggestedActionSet> GetSuggestedActions(
        ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
    {
        var problems = ProblemsIn(range);
        if (problems.Count == 0)
            return Enumerable.Empty<SuggestedActionSet>();

        // Low, so the language's own fixes are not pushed down. Visual Studio
        // 2026 still lists this set first, above them.
        return new[]
        {
            new SuggestedActionSet(
                PredefinedSuggestedActionCategoryNames.CodeFix,
                new ISuggestedAction[] { new FixInChatAction(problems) },
                title: null,
                priority: SuggestedActionSetPriority.Low),
        };
    }

    private IReadOnlyList<Problem> ProblemsIn(SnapshotSpan range)
    {
        try
        {
            if (!_documents.TryGetTextDocument(_buffer, out var document))
                return Array.Empty<Problem>();

            // The Error List's lines are 0-based, like the snapshot's.
            var snapshot = range.Snapshot;
            return ErrorListProblems.OnLines(
                document.FilePath,
                snapshot.GetLineNumberFromPosition(range.Start),
                snapshot.GetLineNumberFromPosition(range.End));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The light bulb is shared with the language's own fixes; a
            // failure here must not cost the user those.
            Serilog.Log.Debug(ex, "[FixInChat] Could not read the problems for the light bulb");
            return Array.Empty<Problem>();
        }
    }

    public bool TryGetTelemetryId(out Guid telemetryId)
    {
        telemetryId = Guid.Empty;
        return false;
    }

    public void Dispose() => ErrorListProblems.Changed -= OnProblemsChanged;
}

internal sealed class FixInChatAction : ISuggestedAction
{
    private readonly IReadOnlyList<Problem> _problems;

    public FixInChatAction(IReadOnlyList<Problem> problems)
    {
        _problems = problems;
    }

    public string DisplayText => FixInChat.DisplayName;

    public bool HasActionSets => false;
    public bool HasPreview => false;
    public ImageMoniker IconMoniker => FixInChat.Icon;
    public string? IconAutomationText => null;
    public string? InputGestureText => null;

    public Task<IEnumerable<SuggestedActionSet>?> GetActionSetsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SuggestedActionSet>?>(null);

    public Task<object?> GetPreviewAsync(CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public void Invoke(CancellationToken cancellationToken)
    {
        // The light bulb waits for Invoke to return; the chat may first have to open.
        VsAgenticPackage.StartFixInChat(_problems);
    }

    public bool TryGetTelemetryId(out Guid telemetryId)
    {
        telemetryId = Guid.Empty;
        return false;
    }

    public void Dispose() { }
}

/// <summary>What every place that offers the fix shows, so they read the same.</summary>
internal static class FixInChat
{
    public const string DisplayName = "Fix in VsAgentic Chat";

    /// <summary>
    /// For menus and the light bulb, which take only catalog images: a chat
    /// bubble with a sparkle, until VsAgentic has an icon of its own.
    /// </summary>
    public static ImageMoniker Icon => KnownMonikers.CommentSparkle;

    // The orange of the chat's "split across agents" lightning icon, so the
    // two read as VsAgentic's own. Measured from a screenshot of that emoji
    // (Segoe UI Emoji draws its own colour), not taken from a theme resource.
    private static readonly System.Windows.Media.Brush Orange = Frozen(0xFF, 0x82, 0x2D);

    // A darker rim keeps the bubble's edge on light and orange-ish backgrounds.
    private static readonly System.Windows.Media.Brush OrangeRim = Frozen(0xC2, 0x56, 0x0F);

    private static readonly System.Windows.Media.Geometry Bubble = FrozenGeometry(
        "M3,2 H13 A2,2 0 0 1 15,4 V10 A2,2 0 0 1 13,12 H7.5 L3.5,15 V12 H3 A2,2 0 0 1 1,10 V4 A2,2 0 0 1 3,2 Z");

    private static readonly System.Windows.Media.Geometry Sparkle = FrozenGeometry(
        "M8,3.5 Q8.5,6.5 11.5,7 Q8.5,7.5 8,10.5 Q7.5,7.5 4.5,7 Q7.5,6.5 8,3.5 Z");

    /// <summary>
    /// The icon drawn in the editor's margin: an orange bubble with a darker
    /// rim and a white sparkle, which stands out from the grey glyphs around
    /// it. The same in every theme; the brushes are fixed, so VS image
    /// theming does not touch them.
    /// </summary>
    public static System.Windows.FrameworkElement CreateIcon(double size)
    {
        var canvas = new System.Windows.Controls.Canvas { Width = 16, Height = 16, Background = System.Windows.Media.Brushes.Transparent };
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Bubble, Fill = Orange, Stroke = OrangeRim, StrokeThickness = 1, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round });
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Sparkle, Fill = System.Windows.Media.Brushes.White });

        return new System.Windows.Controls.Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            Stretch = System.Windows.Media.Stretch.Uniform,
        };
    }

    // Frozen, so the one shared instance can be used from any thread.
    private static System.Windows.Media.Geometry FrozenGeometry(string path)
    {
        var geometry = System.Windows.Media.Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>The tooltip for an icon that offers the fix for these problems.</summary>
    public static string Tooltip(IReadOnlyList<Problem> problems)
    {
        var lines = problems.Take(5).Select(p => p.Code is null ? p.Text : $"{p.Code}: {p.Text}");
        var more = problems.Count > 5 ? $"\n… and {problems.Count - 5} more" : "";
        return $"{DisplayName}\n{string.Join("\n", lines)}{more}";
    }
}
