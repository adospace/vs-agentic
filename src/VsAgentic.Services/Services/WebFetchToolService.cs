using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using ReverseMarkdown;
using VsAgentic.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.Services;

public class WebFetchToolService(
    IOutputListener outputListener,
    ILogger<WebFetchToolService> logger) : IWebFetchToolService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const int MaxHtmlBytes = 512 * 1024;   // 512 KB raw HTML safety cap
    private const int MaxMarkdownChars = 100_000;   // ~100 KB Markdown output limit

    private static readonly Regex StripBlocksRegex = new(
        @"<\s*(script|style|svg|noscript|nav|footer|header)\b[^>]*>[\s\S]*?</\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CollapseBlankLinesRegex = new(
        @"(\r?\n\s*){3,}",
        RegexOptions.Compiled);

    public async Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        logger.LogTrace("[WebFetch] Args received — url: {Url}", url);

        if (string.IsNullOrWhiteSpace(url))
            return new WebFetchResult("", 0, false, "URL cannot be empty.");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new WebFetchResult("", 0, false, "URL must start with http:// or https://.");

        string domain;
        try { domain = new Uri(url).Host; }
        catch { domain = url; }

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "WebFetch",
            Title = $"Fetch: {domain}",
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        try
        {
            logger.LogDebug("Fetching URL: {Url}", url);

            using var response = await HttpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                logger.LogWarning("Fetch failed for {Url}: {Error}", url, error);

                item.Status = OutputItemStatus.Error;
                item.Body = error;
                outputListener.OnStepCompleted(item);
                return new WebFetchResult("", 0, false, error);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("text/html") &&
                !contentType.Contains("text/plain") &&
                !contentType.Contains("application/xhtml"))
            {
                var error = $"Unsupported content type: {contentType}. Only HTML and plain text are supported.";
                logger.LogWarning("Unsupported content type for {Url}: {ContentType}", url, contentType);

                item.Status = OutputItemStatus.Error;
                item.Body = error;
                outputListener.OnStepCompleted(item);
                return new WebFetchResult("", 0, false, error);
            }

            var html = await response.Content.ReadAsStringAsync();

            // Hard safety limit on raw HTML
            if (html.Length > MaxHtmlBytes)
                html = html.Substring(0, MaxHtmlBytes);

            var markdown = ConvertHtmlToMarkdown(html);
            var originalLength = markdown.Length;
            var truncated = false;

            if (markdown.Length > MaxMarkdownChars)
            {
                // Truncate at a line boundary
                var cutoff = markdown.LastIndexOf('\n', MaxMarkdownChars);
                if (cutoff < 0) cutoff = MaxMarkdownChars;
                markdown = markdown.Substring(0, cutoff);
                truncated = true;
            }

            logger.LogDebug("Fetched {Url}: {Length} chars of Markdown (truncated: {Truncated})", url, originalLength, truncated);

            item.Status = OutputItemStatus.Success;
            item.Body = $"Fetched `{domain}` — {originalLength:N0} chars";
            outputListener.OnStepCompleted(item);

            return new WebFetchResult(markdown, originalLength, truncated, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var error = "Request timed out after 30 seconds.";
            logger.LogWarning("Fetch timed out for {Url}", url);

            item.Status = OutputItemStatus.Error;
            item.Body = error;
            outputListener.OnStepCompleted(item);
            return new WebFetchResult("", 0, false, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch {Url}", url);

            item.Status = OutputItemStatus.Error;
            item.Body = $"Error: {ex.Message}";
            outputListener.OnStepCompleted(item);
            return new WebFetchResult("", 0, false, ex.Message);
        }
    }

    private static string ConvertHtmlToMarkdown(string html)
    {
        // Strip noisy blocks before conversion
        html = StripBlocksRegex.Replace(html, "");

        var converter = new Converter(new ReverseMarkdown.Config
        {
            UnknownTags = Config.UnknownTagsOption.Drop,
            RemoveComments = true,
            SmartHrefHandling = true
        });

        var markdown = converter.Convert(html);

        // Collapse excessive blank lines
        markdown = CollapseBlankLinesRegex.Replace(markdown, "\n\n");

        return markdown.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("VsAgentic/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,text/plain,application/xhtml+xml");

        return client;
    }
}
