using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class GrebTool
{
    public static AIFunction Create(IGrebToolService grebService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The regular expression pattern to search for in file contents. Supports full regex syntax (e.g. 'log.*Error', 'function\\s+\\w+').")] string pattern,
                   [Description("Optional glob pattern to filter which files to search (e.g. '*.cs', '**/*.json', 'src/**/*.ts'). If not specified, searches all files recursively.")] string? glob,
                   [Description("Optional directory to search in. If not specified, searches from the project working directory.")] string? path,
                   [Description("Case-insensitive search. Defaults to false.")] bool caseInsensitive,
                   [Description("Number of lines to show before each match. Defaults to 0.")] int contextBefore,
                   [Description("Number of lines to show after each match. Defaults to 0.")] int contextAfter,
                   [Description("If true, only return file paths that contain matches instead of the matching lines. Defaults to false.")] bool filesOnly,
                   CancellationToken cancellationToken) =>
            {
                var options = new GrebOptions
                {
                    Glob = glob,
                    Path = path,
                    CaseInsensitive = caseInsensitive,
                    ContextBefore = contextBefore,
                    ContextAfter = contextAfter,
                    FilesOnly = filesOnly
                };

                var result = await grebService.SearchAsync(pattern, options, cancellationToken);
                return FormatResult(result, filesOnly);
            },
            new AIFunctionFactoryOptions
            {
                Name = "greb",
                Description = "Search file contents using regular expressions, similar to Unix grep. Use this instead of 'bash grep' or 'bash rg' for searching code. Supports regex patterns, file glob filtering, case-insensitive search, and context lines. Returns matching lines with file paths and line numbers."
            });
    }

    private static string FormatResult(GrebResult result, bool filesOnly)
    {
        var parts = new List<string>();

        if (filesOnly)
        {
            if (result.MatchedFiles.Count > 0)
                parts.Add(string.Join("\n", result.MatchedFiles));
            else
                parts.Add("[no matches]");
        }
        else
        {
            if (result.Matches.Count > 0)
                parts.Add(string.Join("\n", result.Matches.Select(m => $"{m.File}:{m.LineNumber}: {m.Line}")));
            else
                parts.Add("[no matches]");
        }

        if (!string.IsNullOrEmpty(result.Error))
            parts.Add($"[note]: {result.Error}");

        return string.Join("\n", parts);
    }
}
