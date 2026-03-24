using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

namespace VsAgentic.Services.Tools;

public static class WebFetchTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "url": { "type": "string", "description": "The URL to fetch. Must be an http:// or https:// URL." }
        },
        "required": ["url"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IWebFetchToolService webFetchService)
    {
        return new ToolDefinition
        {
            Name = "web_fetch",
            Description = "Fetch a web page and return its content as Markdown. Use this to read documentation, API references, release notes, or any web content the user links to. The page HTML is converted to clean Markdown with scripts/styles/nav stripped. Does not support JavaScript-rendered pages. Large pages are automatically truncated at ~100 KB.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var url = input.GetProperty("url").GetString()!;
                var result = await webFetchService.FetchAsync(url, ct);
                return ToolLogger.LogResult("WebFetch", FormatResult(result));
            }
        };
    }

    private static string FormatResult(WebFetchResult result)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(result.Content))
            parts.Add(result.Content);
        else if (string.IsNullOrEmpty(result.Error))
            parts.Add("[no content]");
        if (result.Truncated)
            parts.Add($"[content truncated — original size: {result.ContentLength} chars]");
        if (!string.IsNullOrEmpty(result.Error))
            parts.Add($"[error]: {result.Error}");
        var output = parts.Count > 0 ? string.Join("\n", parts) : "[no content]";
        return OutputSpillHelper.SpillIfNeeded(output, "web_fetch");
    }
}
