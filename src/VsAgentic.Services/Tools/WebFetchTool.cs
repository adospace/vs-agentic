using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Services;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class WebFetchTool
{
    public static AIFunction Create(IWebFetchToolService webFetchService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The URL to fetch. Must be an http:// or https:// URL.")] string url,
                   CancellationToken cancellationToken) =>
            {
                var result = await webFetchService.FetchAsync(url, cancellationToken);
                return FormatResult(result);
            },
            new AIFunctionFactoryOptions
            {
                Name = "web_fetch",
                Description = "Fetch a web page and return its content as Markdown. Use this to read documentation, API references, release notes, or any web content the user links to. The page HTML is converted to clean Markdown with scripts/styles/nav stripped. Does not support JavaScript-rendered pages. Large pages are automatically truncated at ~100 KB."
            });
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
