using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class WriteTool
{
    public static AIFunction Create(IWriteToolService writeService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The absolute or relative path to the file to write. Parent directories are created automatically if they don't exist.")] string filePath,
                   [Description("The full content to write to the file. This will replace the entire file contents if the file already exists. IMPORTANT: Preserve the exact indentation style (tabs vs spaces) and formatting of the existing codebase. Do NOT include line numbers from the read tool output — only the actual file content.")] string content,
                   CancellationToken cancellationToken) =>
            {
                var result = await writeService.WriteAsync(filePath, content, cancellationToken);
                return ToolLogger.LogResult("Write", FormatResult(result));
            },
            new AIFunctionFactoryOptions
            {
                Name = "write",
                Description = "Write content to a file, creating it if it doesn't exist or overwriting if it does. Existing files must have been read first using the read tool to prevent blind overwrites. Parent directories are created automatically. Prefer the edit tool for modifying existing files — use write only for creating new files or complete rewrites."
            });
    }

    private static string FormatResult(WriteResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
            return $"[error]: {result.Error}";

        var action = result.Created ? "created" : "overwritten";
        return $"[ok] File {action} successfully.";
    }
}
