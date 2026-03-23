using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Services;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class GropTool
{
    public static AIFunction Create(IGropToolService gropService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The glob pattern to match file/directory names against. Supports wildcards: * (any characters), ? (single character), ** (recursive directory traversal). Examples: '*.cs', 'src/**/*.json', 'Controllers/*Controller.cs'.")] string pattern,
                   [Description("Optional directory to search in. If not specified, searches from the project working directory. Use relative paths from the project root.")] string? path,
                   CancellationToken cancellationToken) =>
            {
                var result = await gropService.FindAsync(pattern, path, cancellationToken);
                return FormatResult(result);
            },
            new AIFunctionFactoryOptions
            {
                Name = "grop",
                Description = "Find files and directories matching a glob pattern. Use this instead of 'bash ls' for more compact, targeted file discovery. Supports wildcards (*, ?, **) for flexible pattern matching. Returns relative paths sorted alphabetically."
            });
    }

    private static string FormatResult(GropResult result)
    {
        var parts = new List<string>();

        if (result.Matches.Count > 0)
            parts.Add(string.Join("\n", result.Matches));
        else
            parts.Add("[no matches]");

        if (!string.IsNullOrEmpty(result.Error))
            parts.Add($"[note]: {result.Error}");

        var output = string.Join("\n", parts);
        return OutputSpillHelper.SpillIfNeeded(output, "grop");
    }
}
