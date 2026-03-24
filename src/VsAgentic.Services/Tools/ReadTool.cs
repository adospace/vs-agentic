using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class ReadTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "filePath": { "type": "string", "description": "The path to the file to read. Can be absolute or relative to the working directory." },
            "offset": { "type": "integer", "description": "Zero-based line offset to start reading from. Defaults to 0 (beginning of file)." },
            "limit": { "type": "integer", "description": "Maximum number of lines to read. Defaults to 200 lines. Use offset and limit to paginate through larger files." }
        },
        "required": ["filePath"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IReadToolService readService)
    {
        return new ToolDefinition
        {
            Name = "read",
            Description = "Read the contents of a file with line numbers. Returns up to 200 lines by default. Use offset and limit to read specific sections of larger files — when you already know which part you need, only read that part. Prefer this over bash cat/head/tail.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var filePath = input.GetProperty("filePath").GetString()!;
                int? offset = input.TryGetProperty("offset", out var o) ? o.GetInt32() : null;
                int? limit = input.TryGetProperty("limit", out var l) ? l.GetInt32() : null;
                var result = await readService.ReadAsync(filePath, offset, limit, ct);
                return ToolLogger.LogResult("Read", FormatResult(result));
            }
        };
    }

    private static string FormatResult(ReadResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(result.Content))
            parts.Add(result.Content);
        else if (string.IsNullOrEmpty(result.Error))
            parts.Add("[empty file]");
        if (result.Truncated)
            parts.Add($"[total lines in file: {result.TotalLines}]");
        if (!string.IsNullOrEmpty(result.Error))
            parts.Add($"[error]: {result.Error}");
        return parts.Count > 0 ? string.Join("\n", parts) : "[empty file]";
    }
}
