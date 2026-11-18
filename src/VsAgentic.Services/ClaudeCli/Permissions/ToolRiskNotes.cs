using System;
using System.Collections.Generic;
using System.Text.Json;
using VsAgentic.Services.Permissions;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Reads the command or the file out of a Claude tool call and hands it to
/// <see cref="RiskNotes"/>. The patterns themselves do not know Claude's tool
/// names.
/// </summary>
public static class ToolRiskNotes
{
    public static IReadOnlyList<string> For(string toolName, JsonElement input, string? workingDirectory)
    {
        if (input.ValueKind != JsonValueKind.Object) return Array.Empty<string>();

        if (toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase))
            return RiskNotes.ForCommand(ReadString(input, "command"), posix: true);

        if (toolName.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
            return RiskNotes.ForCommand(ReadString(input, "command"), posix: false);

        if (toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase))
            return RiskNotes.ForFileChange(
                ReadString(input, "file_path") ?? ReadString(input, "notebook_path"), workingDirectory);

        return Array.Empty<string>();
    }

    private static string? ReadString(JsonElement input, string property) =>
        input.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
