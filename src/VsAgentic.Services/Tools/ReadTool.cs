using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class ReadTool
{
    public static AIFunction Create(IReadToolService readService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The path to the file to read. Can be absolute or relative to the working directory.")] string filePath,
                   [Description("Zero-based line offset to start reading from. Defaults to 0 (beginning of file).")] int? offset,
                   [Description("Maximum number of lines to read. Defaults to 200 lines. Use offset and limit to paginate through larger files.")] int? limit,
                   CancellationToken cancellationToken) =>
            {
                var result = await readService.ReadAsync(filePath, offset, limit, cancellationToken);
                return ToolLogger.LogResult("Read", FormatResult(result));
            },
            new AIFunctionFactoryOptions
            {
                Name = "read",
                Description = "Read the contents of a file with line numbers. Returns up to 200 lines by default. Use offset and limit to read specific sections of larger files — when you already know which part you need, only read that part. Prefer this over bash cat/head/tail."
            });
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
