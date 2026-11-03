using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsAgentic.Services.IdeContext;
using VsAgentic.VSExtension.Editor;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// The Error List as IDE context. Claude gets the errors and warnings the
/// user sees, with the same codes and positions, instead of building the
/// solution and parsing the output itself.
/// </summary>
internal sealed class DiagnosticsIdeContextProvider : IIdeContextProvider
{
    private const int DefaultMaxResults = 50;
    private const int MaxMaxResults = 200;

    private const string BuildOnlyNote =
        "The Error List is set to \"Build Only\", so Visual Studio is not producing live entries " +
        "(IntelliSense, analyzers); only entries from the last build are included. Code changed since " +
        "that build may have errors not listed here. The user can switch the filter to \"Build + IntelliSense\".";

    private readonly ErrorListReader _reader;
    private readonly EditorContextTracker? _editor;

    public IReadOnlyList<IdeTool> Tools { get; }

    public DiagnosticsIdeContextProvider(ErrorListReader reader, EditorContextTracker? editor)
    {
        _reader = reader;
        _editor = editor;
        Tools = new[]
        {
            new IdeTool(
                "get_diagnostics",
                "Reading the Error List",
                "Returns the errors, warnings and messages in the Visual Studio Error List: code, " +
                "message, file, line and column (1-based), project, and whether the entry comes from " +
                "the last build or from live analysis (IntelliSense and analyzers, updated as the user " +
                "types). Prefer this to building the solution to find compile errors. Suppressed " +
                "entries are left out. When isStale is true, Visual Studio was still updating the list; " +
                "when note is set, it says why the list may be incomplete. After editing code to fix " +
                "diagnostics, call this tool again to check that they are gone, instead of building. " +
                "Visual Studio re-analyzes changed files in the background, so if a fixed entry is " +
                "still listed, wait a few seconds and call again.",
                """
                {"type":"object","properties":{
                  "severity":{"type":"string","enum":["error","warning","message"],"description":"The lowest severity to return: error = errors only, warning = errors and warnings (default), message = everything."},
                  "source":{"type":"string","enum":["all","build","live"],"description":"build = from the last build, live = from IntelliSense and analyzers, all = both (default)."},
                  "scope":{"type":"string","enum":["solution","project","document"],"description":"solution (default), one project, or one document."},
                  "document":{"type":"string","description":"For scope=document: the file path. Default: the file open in the editor."},
                  "project":{"type":"string","description":"For scope=project: the project name as the Error List shows it."},
                  "maxResults":{"type":"integer","minimum":1,"maximum":200,"description":"Default 50."}
                }}
                """,
                GetDiagnosticsAsync,
                // What the user sees in the Error List; Claude could get the
                // same by building. No banner.
                isReadOnly: true),
        };
    }

    private async Task<IdeToolResult> GetDiagnosticsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var minSeverity = GetString(arguments, "severity") switch
        {
            "error" => __VSERRORCATEGORY.EC_ERROR,
            "message" or "all" => __VSERRORCATEGORY.EC_MESSAGE,
            _ => __VSERRORCATEGORY.EC_WARNING,
        };
        var source = GetString(arguments, "source") ?? "all";
        var scope = GetString(arguments, "scope") ?? "solution";
        var maxResults = arguments.TryGetProperty("maxResults", out var max) && max.TryGetInt32(out var n)
            ? Math.Max(1, Math.Min(MaxMaxResults, n))
            : DefaultMaxResults;

        string? document = null;
        string? project = null;
        if (scope == "document")
            document = GetString(arguments, "document");
        else if (scope == "project")
        {
            project = GetString(arguments, "project");
            if (project is null)
                return IdeToolResult.Error("scope=project needs a project name.");
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (scope == "document" && document is null)
            document = _editor?.ReadActive()?.FilePath;
        var liveShown = (Package.GetGlobalService(typeof(SVsErrorList)) as IErrorList)?.AreOtherErrorSourceEntriesShown;
        await TaskScheduler.Default;

        if (scope == "document" && document is null)
            return IdeToolResult.Error("scope=document needs a document path; no file is open in the editor.");

        var snapshot = _reader.Read();
        var inScope = Visible(snapshot.Entries)
            .Where(e => source switch
            {
                "build" => e.IsBuild,
                "live" => !e.IsBuild,
                _ => true,
            })
            .Where(e => document is null || SamePath(e.Document, document))
            .Where(e => project is null || string.Equals(e.Project, project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matching = Order(inScope.Where(e => e.Severity <= minSeverity)).ToList();
        var items = matching.Take(maxResults).Select(e => new
        {
            severity = SeverityName(e.Severity),
            code = e.Code,
            message = e.Text,
            file = e.Document,
            line = e.Line >= 0 ? e.Line + 1 : (int?)null,
            column = e.Column >= 0 ? e.Column + 1 : (int?)null,
            project = e.Project,
            source = e.IsBuild ? "build" : "live",
            tool = e.Tool,
        }).ToList();

        return IdeToolResult.Success(JsonSerializer.Serialize(new
        {
            capturedAt = DateTimeOffset.Now.ToString("o"),
            isStale = !snapshot.IsStable,
            version = snapshot.Version,
            totals = new
            {
                errors = inScope.Count(e => e.Severity == __VSERRORCATEGORY.EC_ERROR),
                warnings = inScope.Count(e => e.Severity == __VSERRORCATEGORY.EC_WARNING),
                messages = inScope.Count(e => e.Severity == __VSERRORCATEGORY.EC_MESSAGE),
            },
            returned = items.Count,
            truncated = matching.Count > items.Count,
            // With the window set to Build Only, Visual Studio does not compute
            // live entries at all, so an empty list would mislead.
            note = liveShown == false && source != "build" ? BuildOnlyNote : null,
            items,
        }));
    }

    /// <summary>
    /// What the Error List shows by default: no suppressed entries, and one
    /// copy of an entry that every target framework of a project reports.
    /// </summary>
    private static IEnumerable<ErrorListEntry> Visible(IEnumerable<ErrorListEntry> entries)
        => entries
            .Where(e => !e.IsSuppressed)
            .OrderBy(e => e.IsActiveContext ? 0 : 1)
            .GroupBy(e => (e.Severity, e.Code, e.Text, Document: e.Document?.ToUpperInvariant(), e.Line, e.Column, e.IsBuild))
            .Select(g => g.First());

    private static IEnumerable<ErrorListEntry> Order(IEnumerable<ErrorListEntry> entries)
        => entries
            .OrderBy(e => e.Severity)
            .ThenBy(e => e.Document ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column);

    private static string SeverityName(__VSERRORCATEGORY severity) => severity switch
    {
        __VSERRORCATEGORY.EC_ERROR => "error",
        __VSERRORCATEGORY.EC_WARNING => "warning",
        _ => "message",
    };

    private static string? GetString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool SamePath(string? a, string b)
    {
        if (a is null) return false;
        try
        {
            // A relative path names the file by its tail, e.g. src\Foo.cs.
            if (!Path.IsPathRooted(b))
            {
                var tail = b.Replace('/', '\\').TrimStart('.', '\\');
                return a.Replace('/', '\\').EndsWith("\\" + tail, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
