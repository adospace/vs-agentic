using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class BashTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "command": { "type": "string", "description": "The bash command to execute. Use Unix-style commands (ls, cat, grep, find, git, etc.)." },
            "title": { "type": "string", "description": "A short human-readable title (3-8 words) describing what this command does, shown to the user. E.g. 'List project files', 'Search for API endpoints', 'Check git status'." }
        },
        "required": ["command", "title"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IBashToolService bashService)
    {
        return new ToolDefinition
        {
            Name = "bash",
            Description = "Execute a bash command via Git Bash. Use this ONLY for operations not covered by other tools: git commands, running builds, executing scripts, installing packages, etc. Do NOT use bash for file search (use grop), content search (use greb), or file reading. Commands run in the project working directory.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var command = input.GetProperty("command").GetString()!;
                var title = input.GetProperty("title").GetString()!;
                var result = await bashService.ExecuteAsync(command, title, ct);
                return ToolLogger.LogResult("Bash", FormatResult(result));
            }
        };
    }

    private static string FormatResult(BashResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(result.StandardOutput))
            parts.Add(result.StandardOutput);
        if (!string.IsNullOrEmpty(result.StandardError))
            parts.Add($"[stderr]: {result.StandardError}");
        if (result.ExitCode != 0)
            parts.Add($"[exit code: {result.ExitCode}]");
        var output = parts.Count > 0 ? string.Join("\n", parts) : "[no output]";
        return OutputSpillHelper.SpillIfNeeded(output, "bash");
    }
}
