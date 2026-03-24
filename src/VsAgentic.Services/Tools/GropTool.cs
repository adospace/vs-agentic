using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class GropTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "pattern": { "type": "string", "description": "The glob pattern to match file/directory names against. Supports wildcards: * (any characters), ? (single character), ** (recursive directory traversal). Examples: '*.cs', 'src/**/*.json', 'Controllers/*Controller.cs'." },
            "path": { "type": "string", "description": "Optional directory to search in. If not specified, searches from the project working directory. Use relative paths from the project root." }
        },
        "required": ["pattern"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IGropToolService gropService)
    {
        return new ToolDefinition
        {
            Name = "grop",
            Description = "Find files and directories matching a glob pattern. Use this instead of 'bash ls' for more compact, targeted file discovery. Supports wildcards (*, ?, **) for flexible pattern matching. Returns relative paths sorted alphabetically.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var pattern = input.GetProperty("pattern").GetString()!;
                string? path = input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var result = await gropService.FindAsync(pattern, path, ct);
                return ToolLogger.LogResult("Grop", FormatResult(result));
            }
        };
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
