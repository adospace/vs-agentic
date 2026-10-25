using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using VsAgentic.Services.IdeContext;
using VsAgentic.VSExtension.Editor;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The C# and Visual Basic code as Visual Studio understands it: which symbol
/// the caret is on, where a symbol is used and what implements it. Claude
/// gets the compiler's answer, which tells overloads and same-named symbols
/// apart, instead of searching the text.
///
/// Reads the solution Visual Studio has loaded, unsaved edits included.
/// </summary>
internal sealed class RoslynIdeContextProvider : IIdeContextProvider
{
    private const int DefaultMaxReferences = 25;
    private const int DefaultMaxImplementations = 25;
    private const int MaxMaxResults = 200;

    private const string QueryProperties = """
        "symbolId":{"type":"string","description":"The symbolId from an earlier result of these tools. Names one symbol exactly, overload included."},
        "name":{"type":"string","description":"A type or member declared in the solution, e.g. OrderService or OrderService.SaveAsync; add parameter types to pick an overload: SaveAsync(Order)."},
        "file":{"type":"string","description":"With line: the symbol at this position. Default, with no other argument: the caret in the editor."},
        "line":{"type":"integer","minimum":1,"description":"1-based."},
        "column":{"type":"integer","minimum":1,"description":"1-based; default 1."}
        """;

    private readonly IComponentModel _componentModel;
    private readonly EditorContextTracker? _editor;
    private VisualStudioWorkspace? _workspace;

    public IReadOnlyList<IdeTool> Tools { get; }

    public RoslynIdeContextProvider(IComponentModel componentModel, EditorContextTracker? editor)
    {
        _componentModel = componentModel;
        _editor = editor;
        Tools = new[]
        {
            new IdeTool(
                "get_symbol_context",
                "Looking up the symbol",
                "Describes a C# or Visual Basic symbol as the compiler sees it: kind, containing type and " +
                "namespace, type or return type, parameters, accessibility, modifiers, where it is declared, " +
                "and its documentation summary. With no arguments: the symbol under the caret in the " +
                "Visual Studio editor, and the member the caret is in. Use it when the user says \"this " +
                "method\" or \"this class\". Covers unsaved edits. Returns a symbolId to pass to " +
                "find_references and find_implementations. A name that matches several symbols returns " +
                "the candidates.",
                "{\"type\":\"object\",\"properties\":{" + QueryProperties + "}}",
                GetSymbolContextAsync),
            new IdeTool(
                "find_references",
                "Finding references",
                "Finds the references to a C# or Visual Basic symbol in the open solution, as Visual " +
                "Studio's Find All References does: by meaning, not by text, so overloads, same-named " +
                "symbols, comments and strings are told apart. Prefer this to searching text when asked " +
                "where something is used, or before changing a signature. With no arguments: the symbol " +
                "under the caret. Returns file, line, column and the line's text; results are sorted by " +
                "file, and skip continues a truncated list.",
                "{\"type\":\"object\",\"properties\":{" + QueryProperties + "," +
                "\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"description\":\"Default 25.\"}," +
                "\"skip\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"References to skip, to continue a truncated list.\"}}}",
                FindReferencesAsync),
            new IdeTool(
                "find_implementations",
                "Finding implementations",
                "Finds what implements a C# or Visual Basic interface, interface member, or abstract or " +
                "virtual member in the open solution, as Visual Studio's Go To Implementation does. With " +
                "no arguments: the symbol under the caret.",
                "{\"type\":\"object\",\"properties\":{" + QueryProperties + "," +
                "\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"description\":\"Default 25.\"}}}",
                FindImplementationsAsync),
        };
    }

    public async Task<IdeContextSection?> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var (resolution, _) = await ResolveAsync(new SymbolQuery(null, null, null, null, null), cancellationToken);
        if (resolution?.Symbol is not { } symbol)
            return null;

        var summary = $"Caret on {RoslynSymbols.KindName(symbol)} {RoslynSymbols.Display(symbol)}";
        if (resolution.Enclosing is { } enclosing && !SymbolEqualityComparer.Default.Equals(enclosing, symbol))
            summary += $" in {RoslynSymbols.Display(enclosing)}";

        return new IdeContextSection("symbol", summary, "Roslyn", DateTimeOffset.Now, detailsAvailable: true);
    }

    private async Task<IdeToolResult> GetSymbolContextAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (resolution, error) = await ResolveAsync(ReadQuery(arguments), cancellationToken);
        if (resolution is null)
            return IdeToolResult.Error(error!);
        if (resolution.Candidates.Count > 0)
            return Json(RoslynSymbols.Candidates(resolution));
        if (resolution.Project is not { } project || (resolution.Symbol is null && resolution.Enclosing is null))
            return Json(new { symbol = (object?)null, note = resolution.Note });

        return Json(new
        {
            capturedAt = DateTimeOffset.Now.ToString("o"),
            symbol = resolution.Symbol is { } symbol
                ? await RoslynSymbols.DescribeAsync(symbol, project, cancellationToken)
                : null,
            enclosing = resolution.Enclosing is { } enclosing && !SymbolEqualityComparer.Default.Equals(enclosing, resolution.Symbol)
                ? RoslynSymbols.Brief(enclosing, project)
                : null,
            note = resolution.Note,
        });
    }

    private async Task<IdeToolResult> FindReferencesAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (resolution, error) = await ResolveAsync(ReadQuery(arguments), cancellationToken);
        if (resolution is null)
            return IdeToolResult.Error(error!);
        if (resolution.Candidates.Count > 0)
            return Json(RoslynSymbols.Candidates(resolution));
        if (resolution.Symbol is not { } symbol || resolution.Project is not { } project)
            return Json(new { symbol = (object?)null, note = resolution.Note });

        var skip = GetInt(arguments, "skip") is { } s ? Math.Max(0, s) : 0;
        return Json(await RoslynSymbols.FindReferencesAsync(
            symbol, project, skip, MaxResults(arguments, DefaultMaxReferences), cancellationToken));
    }

    private async Task<IdeToolResult> FindImplementationsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (resolution, error) = await ResolveAsync(ReadQuery(arguments), cancellationToken);
        if (resolution is null)
            return IdeToolResult.Error(error!);
        if (resolution.Candidates.Count > 0)
            return Json(RoslynSymbols.Candidates(resolution));
        if (resolution.Symbol is not { } symbol || resolution.Project is not { } project)
            return Json(new { symbol = (object?)null, note = resolution.Note });

        return Json(await RoslynSymbols.FindImplementationsAsync(
            symbol, project, MaxResults(arguments, DefaultMaxImplementations), cancellationToken));
    }

    /// <summary>
    /// The query resolved against the current solution; a query with no
    /// symbol, name or file means the caret. Null with an error when the
    /// language services are missing.
    /// </summary>
    private async Task<(SymbolResolution? Resolution, string? Error)> ResolveAsync(SymbolQuery query, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Resolved on first use, not at start-up: it loads the language
        // services, which a chat that never asks about code does not need.
        _workspace ??= _componentModel.GetService<VisualStudioWorkspace>();

        if (query.IsEmpty)
        {
            // An answer, not an error, as in get_editor_context: the user may
            // simply be in a designer or a properties page.
            if (_editor?.ReadCaret() is not { } caret)
            {
                await TaskScheduler.Default;
                return (new SymbolResolution
                {
                    Note = "No text document is open in the editor. Pass a symbolId, a name, or a file and line.",
                }, null);
            }

            query = new SymbolQuery(null, null, caret.Path, caret.Line + 1, caret.Column + 1);
        }

        var workspace = _workspace;
        await TaskScheduler.Default;

        if (workspace is null)
            return (null, "The C# and Visual Basic language services are not available.");

        var resolution = await RoslynSymbols.ResolveAsync(
            workspace.CurrentSolution, query, workspace.GetDocumentIdInCurrentContext, cancellationToken);
        return (resolution, null);
    }

    private static SymbolQuery ReadQuery(JsonElement arguments)
        => new(
            GetString(arguments, "symbolId"),
            GetString(arguments, "name"),
            GetString(arguments, "file"),
            GetInt(arguments, "line"),
            GetInt(arguments, "column"));

    private static int MaxResults(JsonElement arguments, int defaultValue)
        => GetInt(arguments, "maxResults") is { } n ? Math.Max(1, Math.Min(MaxMaxResults, n)) : defaultValue;

    // Without nulls: most fields are empty for most symbols, and every
    // reference would otherwise carry "candidate":null.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static IdeToolResult Json(object value) => IdeToolResult.Success(JsonSerializer.Serialize(value, JsonOptions));

    private static string? GetString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? GetInt(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var n)
            ? n
            : null;
}
