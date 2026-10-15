using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// A <c>resource_link</c> block an MCP tool returned: a file or other resource
/// handed back by reference instead of inline. The CLI collects these from the
/// raw tool result into <c>tool_use_result.resourceLinks</c> on the user event
/// that carries the result, so the host does not have to parse the text the
/// model reads ("[Resource link: name] uri").
/// </summary>
public sealed record McpResourceLink(string Uri, string Name, string? Title, string? Description)
{
    /// <summary>The name to show: the title when the server gave one.</summary>
    public string Label => string.IsNullOrEmpty(Title) ? Name : Title!;

    public bool IsFile => Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a <c>resourceLinks</c> array. Entries without a uri are skipped;
    /// anything that is not an array gives an empty list.
    /// </summary>
    public static IReadOnlyList<McpResourceLink> ParseList(JsonElement links)
    {
        var result = new List<McpResourceLink>();
        if (links.ValueKind != JsonValueKind.Array) return result;

        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object) continue;
            var uri = GetString(link, "uri");
            if (string.IsNullOrEmpty(uri)) continue;
            result.Add(new McpResourceLink(
                uri!,
                GetString(link, "name") ?? uri!,
                GetString(link, "title"),
                GetString(link, "description")));
        }
        return result;
    }

    /// <summary>
    /// Markdown list for the step body. A file becomes a link, which the chat
    /// view opens in the editor like any other file link. Other schemes stay
    /// text: the chat page has no handler for them, and a click would navigate
    /// the page itself.
    /// </summary>
    public static string FormatMarkdown(IReadOnlyList<McpResourceLink> links)
    {
        var sb = new StringBuilder();
        sb.Append("**Returned ").Append(links.Count == 1 ? "1 resource" : $"{links.Count} resources").AppendLine(":**");
        foreach (var link in links)
        {
            sb.Append("- ");
            if (link.IsFile)
                sb.Append('[').Append(EscapeText(link.Label)).Append("](").Append(EscapeUri(link.Uri)).Append(')');
            else
                sb.Append(EscapeText(link.Label)).Append(": `").Append(link.Uri.Replace("`", "%60")).Append('`');

            if (!string.IsNullOrEmpty(link.Description))
                sb.Append(" — ").Append(EscapeText(link.Description!));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // Characters that would end the link target early. The host turns the
    // file URI back into a path, which undoes the escaping.
    private static string EscapeUri(string uri) =>
        uri.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29").Replace("<", "%3C").Replace(">", "%3E");

    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\r' || c == '\n') { sb.Append(' '); continue; }
            // Showdown has no backslash escape for '<'.
            if (c == '<') { sb.Append("&lt;"); continue; }
            if ("\\`*_[]>#|".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
