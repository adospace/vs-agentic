using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Services;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class BashTool
{
    public static AIFunction Create(IBashToolService bashService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The bash command to execute. Use Unix-style commands (ls, cat, grep, find, git, etc.).")] string command,
                   [Description("A short human-readable title (3-8 words) describing what this command does, shown to the user. E.g. 'List project files', 'Search for API endpoints', 'Check git status'.")] string title,
                   CancellationToken cancellationToken) =>
            {
                var result = await bashService.ExecuteAsync(command, title, cancellationToken);
                return ToolLogger.LogResult("Bash", FormatResult(result));
            },
            new AIFunctionFactoryOptions
            {
                Name = "bash",
                Description = "Execute a bash command via Git Bash. Use this ONLY for operations not covered by other tools: git commands, running builds, executing scripts, installing packages, etc. Do NOT use bash for file search (use grop), content search (use greb), or file reading. Commands run in the project working directory."
            });
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
