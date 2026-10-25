using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using VsAgentic.Services.IdeContext;
using VsAgentic.VSExtension.Editor;
using Task = System.Threading.Tasks.Task;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The editor as IDE context: which file is open and what is selected in it.
/// Lets Claude resolve "this" and "here" in a message without the user
/// attaching anything.
/// </summary>
internal sealed class EditorIdeContextProvider : IIdeContextProvider
{
    // The same limits as a selection attached from the editor.
    private const int MaxSelectedLines = 500;
    private const int MaxSelectedChars = 30_000;

    private readonly EditorContextTracker _tracker;

    public IReadOnlyList<IdeTool> Tools { get; }

    public EditorIdeContextProvider(EditorContextTracker tracker)
    {
        _tracker = tracker;
        Tools = new[]
        {
            new IdeTool(
                "get_editor_context",
                "Reading the editor",
                "Returns the file the user has open in the Visual Studio editor and the lines they " +
                "have selected, with the selected text as the editor holds it (saved or not). Use it " +
                "when the user refers to \"this\", \"here\" or \"the selected code\" without naming a file.",
                """{"type":"object","properties":{}}""",
                GetEditorContextAsync),
        };
    }

    public async Task<IdeContextSection?> GetSummaryAsync(CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (_tracker.ReadActive() is not { } context)
            return null;

        return new IdeContextSection(
            "editor",
            context.HasSelection ? $"Editing {context.Reference} (selection)" : $"Editing {context.FilePath}",
            "Editor",
            DateTimeOffset.Now,
            detailsAvailable: context.HasSelection);
    }

    private async Task<IdeToolResult> GetEditorContextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var context = _tracker.ReadActive();
        await TaskScheduler.Default;

        if (context is null)
            return IdeToolResult.Success("""{"file":null,"note":"No text document is open in the editor."}""");

        string? text = context.SelectedText;
        string? note = null;
        if (text is not null &&
            (context.EndLine - context.StartLine + 1 > MaxSelectedLines || text.Length > MaxSelectedChars))
        {
            text = null;
            note = "The selection is too large to return; read the lines from the file instead.";
        }

        return IdeToolResult.Success(JsonSerializer.Serialize(new
        {
            file = context.FilePath,
            selection = context.HasSelection
                ? new { startLine = context.StartLine, endLine = context.EndLine, text }
                : null,
            note,
        }));
    }
}
