using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class EditTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "filePath": { "type": "string", "description": "The absolute or relative path to the file to edit." },
            "oldString": { "type": "string", "description": "The exact text to find and replace. Must match the file content character-for-character, including whitespace and indentation. If this string appears multiple times, either provide more surrounding context to make it unique or set replaceAll to true." },
            "newString": { "type": "string", "description": "The text to replace old_string with. Must be different from old_string." },
            "replaceAll": { "type": "boolean", "description": "If true, replaces ALL occurrences of old_string. Defaults to false, which requires old_string to be unique in the file." }
        },
        "required": ["filePath", "oldString", "newString"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IEditToolService editService)
    {
        return new ToolDefinition
        {
            Name = "edit",
            Description = "Perform exact string replacements in a file. Finds old_string in the file and replaces it with new_string. By default, old_string must appear exactly once (provide more surrounding context if ambiguous). Set replace_all=true to replace every occurrence. Prefer this over bash sed/awk for file modifications.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var filePath = input.GetProperty("filePath").GetString()!;
                var oldString = input.GetProperty("oldString").GetString()!;
                var newString = input.GetProperty("newString").GetString()!;
                var replaceAll = input.TryGetProperty("replaceAll", out var ra) && ra.ValueKind == JsonValueKind.True;
                var result = await editService.EditAsync(filePath, oldString, newString, replaceAll, ct);
                return ToolLogger.LogResult("Edit", FormatResult(result));
            }
        };
    }

    private static string FormatResult(EditResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
            return $"[error]: {result.Error}";
        var parts = new List<string> { $"[ok] {result.Replacements} replacement(s) applied" };
        if (result.AffectedLines.Count > 0)
            parts.Add($"[affected lines]: {string.Join(", ", result.AffectedLines)}");
        return string.Join("\n", parts);
    }
}
