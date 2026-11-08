using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The text and the images of a <c>tool_result</c> block's <c>content</c>.
///
/// Read on an image file, and an MCP tool that returns one, both arrive as
/// <c>{"type":"image","source":{"type":"base64","media_type":…,"data":…}}</c>.
/// For an MCP image the CLI also saves the file and adds a text block
/// <c>[Image: source: &lt;path&gt;]</c> after it. That line is for the model;
/// the chat shows the image itself, so the line is dropped from the text.
/// </summary>
internal sealed class ToolResultContent
{
    /// <summary>A step shows at most this many images; the rest are counted in the text.</summary>
    internal const int MaxImages = 5;

    /// <summary>Larger images are left out, so one huge capture cannot stall the chat.</summary>
    internal const int MaxImageBytes = 10 * 1024 * 1024;

    private static readonly Regex ImageSourceLine = new(
        @"^\[Image: source: [^\r\n]+\]$", RegexOptions.CultureInvariant);

    public string Text { get; }
    public IReadOnlyList<ChatImageAttachment> Images { get; }

    private ToolResultContent(string text, IReadOnlyList<ChatImageAttachment> images)
    {
        Text = text;
        Images = images;
    }

    public static ToolResultContent Read(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return new ToolResultContent(content.GetString() ?? "", Array.Empty<ChatImageAttachment>());

        if (content.ValueKind != JsonValueKind.Array)
            return new ToolResultContent(content.GetRawText(), Array.Empty<ChatImageAttachment>());

        var images = new List<ChatImageAttachment>();
        var texts = new List<string>();
        var skipped = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = item.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String
                ? tp.GetString()
                : null;

            if (type == "image")
            {
                var image = ReadImage(item);
                if (image is not null && images.Count < MaxImages) images.Add(image);
                else skipped++;
                continue;
            }

            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                texts.Add(text.GetString() ?? "");
        }

        var sb = new StringBuilder();
        foreach (var text in texts)
        {
            // Only next to an image: without one, the line is all the user has.
            if (images.Count > 0 && ImageSourceLine.IsMatch(text.Trim())) continue;
            sb.AppendLine(text);
        }
        if (skipped > 0)
            sb.AppendLine(skipped == 1 ? "*1 image not shown*" : $"*{skipped} images not shown*");

        return new ToolResultContent(sb.ToString().TrimEnd(), images);
    }

    private static ChatImageAttachment? ReadImage(JsonElement item)
    {
        if (!item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) return null;
        if (!source.TryGetProperty("type", out var st) || st.GetString() != "base64") return null;
        if (!source.TryGetProperty("media_type", out var mt) || mt.ValueKind != JsonValueKind.String) return null;
        if (!source.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return null;

        var mediaType = mt.GetString() ?? "";
        if (!ChatImageAttachment.IsSupported(mediaType)) return null;

        // Checked on the base64 length first, so an oversized image is never decoded.
        var encoded = data.GetString() ?? "";
        if (encoded.Length / 4 * 3 > MaxImageBytes) return null;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length == 0 ? null : new ChatImageAttachment(bytes, mediaType);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
