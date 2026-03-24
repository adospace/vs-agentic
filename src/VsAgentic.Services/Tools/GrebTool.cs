using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class GrebTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "pattern": { "type": "string", "description": "The regular expression pattern to search for in file contents. Supports full regex syntax (e.g. 'log.*Error', 'function\\s+\\w+')." },
            "glob": { "type": "string", "description": "Optional glob pattern to filter which files to search (e.g. '*.cs', '**/*.json', 'src/**/*.ts'). If not specified, searches all files recursively." },
            "path": { "type": "string", "description": "Optional directory to search in. If not specified, searches from the project working directory." },
            "caseInsensitive": { "type": "boolean", "description": "Case-insensitive search. Defaults to false." },
            "contextBefore": { "type": "integer", "description": "Number of lines to show before each match. Defaults to 0." },
            "contextAfter": { "type": "integer", "description": "Number of lines to show after each match. Defaults to 0." },
            "filesOnly": { "type": "boolean", "description": "If true, only return file paths that contain matches instead of the matching lines. Defaults to false." }
        },
        "required": ["pattern"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IGrebToolService grebService)
    {
        return new ToolDefinition
        {
            Name = "greb",
            Description = "Search file contents using regular expressions, similar to Unix grep. Use this instead of 'bash grep' or 'bash rg' for searching code. Supports regex patterns, file glob filtering, case-insensitive search, and context lines. Returns matching lines with file paths and line numbers.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var pattern = input.GetProperty("pattern").GetString()!;
                var options = new GrebOptions
                {
                    Glob = input.TryGetProperty("glob", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null,
                    Path = input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                    CaseInsensitive = input.TryGetProperty("caseInsensitive", out var ci) && ci.ValueKind == JsonValueKind.True,
                    ContextBefore = input.TryGetProperty("contextBefore", out var cb) ? cb.GetInt32() : 0,
                    ContextAfter = input.TryGetProperty("contextAfter", out var ca) ? ca.GetInt32() : 0,
                    FilesOnly = input.TryGetProperty("filesOnly", out var fo) && fo.ValueKind == JsonValueKind.True
                };
                var result = await grebService.SearchAsync(pattern, options, ct);
                return ToolLogger.LogResult("Greb", FormatResult(result, options.FilesOnly));
            }
        };
    }

    private static string FormatResult(GrebResult result, bool filesOnly)
    {
        var parts = new List<string>();
        if (filesOnly)
        {
            parts.Add(result.MatchedFiles.Count > 0 ? string.Join("\n", result.MatchedFiles) : "[no matches]");
        }
        else
        {
            parts.Add(result.Matches.Count > 0
                ? string.Join("\n", result.Matches.Select(m => $"{m.File}:{m.LineNumber}: {m.Line}"))
                : "[no matches]");
        }
        if (!string.IsNullOrEmpty(result.Error))
            parts.Add($"[note]: {result.Error}");
        var output = string.Join("\n", parts);
        return OutputSpillHelper.SpillIfNeeded(output, "greb");
    }
}
