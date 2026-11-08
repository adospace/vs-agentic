using System.Text.Json;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The images in a <c>tool_result</c> block's <c>content</c>.
///
/// Read on an image file, and an MCP tool that returns one, both arrive as
/// <c>{"type":"image","source":{"type":"base64","media_type":…,"data":…}}</c>.
/// For an MCP image the CLI converts the MCP form to this one, saves the file
/// and adds a text block <c>[Image: source: &lt;path&gt;]</c> after it.
/// </summary>
internal static class ToolResultImages
{
    /// <summary>A step shows at most this many images; the rest count as skipped.</summary>
    internal const int MaxImages = 5;

    /// <summary>Larger images are skipped, so one huge capture cannot stall the chat.</summary>
    internal const int MaxImageBytes = 10 * 1024 * 1024;

    /// <param name="content">The <c>content</c> of a tool_result block.</param>
    /// <param name="skipped">
    /// Image blocks left out: over the limit, too large, in a format the chat
    /// cannot show, or not readable.
    /// </param>
    public static IReadOnlyList<ChatImageAttachment> Read(JsonElement content, out int skipped)
    {
        skipped = 0;
        if (content.ValueKind != JsonValueKind.Array) return Array.Empty<ChatImageAttachment>();

        var images = new List<ChatImageAttachment>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("type", out var tp) || tp.ValueKind != JsonValueKind.String
                || tp.GetString() != "image") continue;

            var image = images.Count < MaxImages ? ReadImage(item) : null;
            if (image is null) skipped++;
            else images.Add(image);
        }
        return images;
    }

    private static ChatImageAttachment? ReadImage(JsonElement item)
    {
        if (!item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) return null;
        if (!source.TryGetProperty("type", out var st) || st.ValueKind != JsonValueKind.String
            || st.GetString() != "base64") return null;
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
