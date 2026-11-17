using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>
/// Review findings into the Error List. One instance per chat: each report
/// replaces what the same chat reported before, and disposing the chat's
/// services removes its findings.
/// </summary>
internal sealed class ReviewIdeContextProvider : IIdeContextProvider, IDisposable
{
    private const int MaxFindings = 200;

    private const string BuildOnlyNote =
        "The Error List is set to \"Build Only\", so the findings are hidden. Tell the user to switch " +
        "the filter to \"Build + IntelliSense\" to see them.";

    private readonly ReviewFindingsTable _table;
    private readonly string? _workingDirectory;

    public IReadOnlyList<IdeTool> Tools { get; }

    public ReviewIdeContextProvider(ReviewFindingsTable table, string? workingDirectory)
    {
        _table = table;
        _workingDirectory = workingDirectory;
        Tools = new[]
        {
            new IdeTool(
                ReviewTool.Name,
                "Showing the review findings in the Error List",
                "Shows code review findings in the Visual Studio Error List, where the user can sort " +
                "them and double-click one to open the file at the line. Use it whenever you review " +
                "code changes for the user. Send all findings of one review in one call: each call " +
                "replaces the findings this chat reported before. Send an empty list when the review " +
                "found nothing, so the previous findings are cleared. Paths may be absolute or " +
                "relative to the repository root or the solution folder. Lines and columns are " +
                "1-based. The result lists the findings whose file was not found; they are not shown.",
                """
                {"type":"object","required":["findings"],"properties":{
                  "findings":{"type":"array","maxItems":200,"items":{"type":"object","required":["file","line","severity","message"],"properties":{
                    "file":{"type":"string","description":"The file the finding is about."},
                    "line":{"type":"integer","minimum":1,"description":"1-based line."},
                    "column":{"type":"integer","minimum":1,"description":"1-based column. Default 1."},
                    "severity":{"type":"string","enum":["error","warning","message"],"description":"error = a bug or a security problem, warning = a likely problem, message = a suggestion."},
                    "message":{"type":"string","description":"What is wrong and what to do, in one or two sentences."},
                    "category":{"type":"string","description":"One short word for the Code column, e.g. bug, security, performance, style."}
                  }}}
                }}
                """,
                ReportAsync,
                // It reads nothing and changes no file: the findings only
                // appear in the Error List. No banner.
                isReadOnly: true),
        };
    }

    private async Task<IdeToolResult> ReportAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("findings", out var list) || list.ValueKind != JsonValueKind.Array)
            return IdeToolResult.Error("findings must be an array; send an empty array to clear the findings.");

        var findings = new List<ReviewFinding>();
        var notFound = new List<string>();
        var invalid = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (findings.Count == MaxFindings) break;

            var file = GetString(item, "file");
            var message = GetString(item, "message");
            if (file is null || message is null || item.ValueKind != JsonValueKind.Object)
            {
                invalid++;
                continue;
            }

            var path = Resolve(file);
            if (path is null)
            {
                notFound.Add(file);
                continue;
            }

            findings.Add(new ReviewFinding(
                path,
                Math.Max(1, GetInt(item, "line") ?? 1) - 1,
                Math.Max(1, GetInt(item, "column") ?? 1) - 1,
                GetString(item, "severity") switch
                {
                    "error" => __VSERRORCATEGORY.EC_ERROR,
                    "message" => __VSERRORCATEGORY.EC_MESSAGE,
                    _ => __VSERRORCATEGORY.EC_WARNING,
                },
                message,
                GetString(item, "category")));
        }

        _table.Replace(this, findings);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var otherSourcesShown = (Package.GetGlobalService(typeof(SVsErrorList)) as IErrorList)?.AreOtherErrorSourceEntriesShown;
        if (findings.Count > 0)
            ShowErrorList();
        await TaskScheduler.Default;

        return IdeToolResult.Success(JsonSerializer.Serialize(new
        {
            shown = findings.Count,
            notFound,
            invalid,
            truncated = list.GetArrayLength() > findings.Count + notFound.Count + invalid,
            note = otherSourcesShown == false && findings.Count > 0 ? BuildOnlyNote : null,
        }));
    }

    // Shown, not activated: the user may be typing in the chat.
    private static void ShowErrorList()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell shell) return;

        var errorList = VSConstants.StandardToolWindows.ErrorList;
        if (shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref errorList, out var frame) == VSConstants.S_OK)
            frame?.ShowNoActivate();
    }

    /// <summary>
    /// The full path of an existing file, or null. Claude usually takes paths
    /// from git, relative to the repository root, which can sit above the
    /// solution folder (a solution kept in src\). So a relative path is tried
    /// against the solution folder and each parent up to the repository root.
    /// </summary>
    private string? Resolve(string path)
    {
        try
        {
            path = path.Replace('/', '\\');
            // The path comes from the model; a UNC path would make the
            // existence check connect to any host.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;

            if (Path.IsPathRooted(path))
                return File.Exists(path) ? Path.GetFullPath(path) : null;

            if (_workingDirectory is null) return null;

            for (var dir = new DirectoryInfo(_workingDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.GetFullPath(Path.Combine(dir.FullName, path));
                if (File.Exists(candidate)) return candidate;
                // .git is a file, not a folder, in a worktree or a submodule.
                var gitPath = Path.Combine(dir.FullName, ".git");
                if (File.Exists(gitPath) || Directory.Exists(gitPath)) break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // Not a valid path.
        }
        return null;
    }

    private static string? GetString(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? GetInt(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var n)
            ? n
            : null;

    public void Dispose() => _table.Replace(this, Array.Empty<ReviewFinding>());
}
