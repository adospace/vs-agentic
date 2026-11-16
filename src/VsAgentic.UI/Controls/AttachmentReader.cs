using System.IO;
using System.Windows.Media.Imaging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Turns paths and bitmaps into attachments the CLI accepts. Every way of
/// attaching something ends here — the clipboard, a drop on the input box, and
/// the file picker — so all three inline the same kinds of image, shrink them
/// the same way, and stop decoding at the same count.
/// </summary>
public static class AttachmentReader
{
    /// <summary>
    /// Anthropic resizes anything larger than this before the model sees it, and
    /// bills for the full upload either way, so shrink first. Screenshots from a
    /// 4K monitor are several megabytes; base64 adds another third on top.
    /// </summary>
    private const int MaxEdge = 1568;

    /// <summary>
    /// How many images one action sends inline. Every one of them is decoded at
    /// full size before it is shrunk, and that happens on the UI thread, so a
    /// folder of photos copied in File Explorer would otherwise lock up Visual
    /// Studio and then put the whole set into one request as base64. Images past
    /// this many are attached as paths instead: nothing is decoded, nothing is
    /// dropped, and the CLI reads them off disk if it needs them.
    /// </summary>
    private const int MaxInlineImages = 5;

    /// <summary>
    /// Reads a list of paths in the order they were given. An image comes back
    /// decoded, so it can go inline; everything else comes back as a path for
    /// the CLI to open itself. Paths that no longer exist are skipped, so the
    /// result can be shorter than the input, and empty when none of them can be
    /// attached.
    /// </summary>
    public static IReadOnlyList<IChatAttachment> FromPaths(IEnumerable<string?> paths)
    {
        var attachments = new List<IChatAttachment>();
        var inlined = 0;
        foreach (var path in paths)
        {
            var attachment = TryReadPath(path, inlined < MaxInlineImages);
            if (attachment is null) continue;
            if (attachment is ChatImageAttachment) inlined++;
            attachments.Add(attachment);
        }
        return attachments;
    }

    /// <summary>
    /// Takes a bitmap that came from no file — a screenshot off the clipboard —
    /// and shrinks it the same way a copied image file is shrunk.
    /// </summary>
    public static ChatImageAttachment FromBitmap(BitmapSource source) => FromBitmapSource(source);

    /// <param name="mayInline">
    /// False once the action has inlined as many images as it is allowed to. An
    /// image file is then attached as a path without being decoded, which is
    /// what keeps a large selection off the UI thread.
    /// </param>
    private static IChatAttachment? TryReadPath(string? path, bool mayInline)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Directory.Exists(path)) return new ChatFileAttachment(path!);
        if (!File.Exists(path)) return null;
        if (!mayInline) return new ChatFileAttachment(path!);

        try
        {
            var image = TryReadImageFile(path!);
            if (image is not null) return image;
        }
        catch
        {
            // A file that claims to be a PNG but will not decode is still worth
            // attaching — the CLI can go and look at it on disk.
        }

        return new ChatFileAttachment(path!);
    }

    /// <summary>
    /// Decodes an image file, or returns null when the extension is not one the
    /// API takes inline — those go out as paths instead.
    /// </summary>
    private static ChatImageAttachment? TryReadImageFile(string path)
    {
        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
        if (mediaType is null) return null;

        // Re-encode through the decoder so oversized files get shrunk too. GIF is
        // passed through untouched: decoding keeps only the first frame, which
        // would silently drop the animation.
        if (mediaType == "image/gif")
            return new ChatImageAttachment(File.ReadAllBytes(path), mediaType, path);

        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.UriSource = new Uri(path);
        decoded.EndInit();
        return FromBitmapSource(decoded, path);
    }

    /// <param name="sourcePath">
    /// Null for a bitmap off the clipboard, which came from no file the message
    /// could name.
    /// </param>
    private static ChatImageAttachment FromBitmapSource(BitmapSource source, string? sourcePath = null)
    {
        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource final = scale < 1.0
            ? new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale))
            : source;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ChatImageAttachment(stream.ToArray(), "image/png", sourcePath);
    }
}
