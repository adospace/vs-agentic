using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class EditTool
{
    public static AIFunction Create(IEditToolService editService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The absolute or relative path to the file to edit.")] string filePath,
                   [Description("The exact text to find and replace. Must match the file content character-for-character, including whitespace and indentation. If this string appears multiple times, either provide more surrounding context to make it unique or set replaceAll to true.")] string oldString,
                   [Description("The text to replace old_string with. Must be different from old_string.")] string newString,
                   [Description("If true, replaces ALL occurrences of old_string. Defaults to false, which requires old_string to be unique in the file.")] bool replaceAll,
                   CancellationToken cancellationToken) =>
            {
                var result = await editService.EditAsync(filePath, oldString, newString, replaceAll, cancellationToken);
                return FormatResult(result);
            },
            new AIFunctionFactoryOptions
            {
                Name = "edit",
                Description = "Perform exact string replacements in a file. Finds old_string in the file and replaces it with new_string. By default, old_string must appear exactly once (provide more surrounding context if ambiguous). Set replace_all=true to replace every occurrence. Prefer this over bash sed/awk for file modifications."
            });
    }

    private static string FormatResult(EditResult result)
    {
        if (!string.IsNullOrEmpty(result.Error))
            return $"[error]: {result.Error}";

        var parts = new List<string>
        {
            $"[ok] {result.Replacements} replacement(s) applied"
        };

        if (result.AffectedLines.Count > 0)
            parts.Add($"[affected lines]: {string.Join(", ", result.AffectedLines)}");

        return string.Join("\n", parts);
    }
}
