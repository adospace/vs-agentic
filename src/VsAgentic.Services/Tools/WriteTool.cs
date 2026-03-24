using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class WriteTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "filePath": { "type": "string", "description": "The absolute or relative path to the file to write. Parent directories are created automatically if they don't exist." },
            "content": { "type": "string", "description": "The full content to write to the file. This will replace the entire file contents if the file already exists. IMPORTANT: Preserve the exact indentation style (tabs vs spaces) and formatting of the existing codebase. Do NOT include line numbers from the read tool output — only the actual file content." }
        },
        "required": ["filePath", "content"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IWriteToolService writeService)
    {
        return new ToolDefinition
        {
            Name = "write",
            Description = "Write content to a file. If the file exists, it will be overwritten (you must read it first). If it doesn't exist, it will be created along with any missing parent directories.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var filePath = input.GetProperty("filePath").GetString()!;
                var content = input.GetProperty("content").GetString()!;
                var result = await writeService.WriteAsync(filePath, content, ct);
                return ToolLogger.LogResult("Write", FormatResult(result));
            }
        };
    }

    private static string FormatResult(WriteResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
            return $"[error]: {result.Error}";
        return result.Created ? "[ok] File created successfully." : "[ok] File overwritten successfully.";
    }
}
