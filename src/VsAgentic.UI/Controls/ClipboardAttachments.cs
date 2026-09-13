using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Turns whatever the clipboard is holding into attachments the CLI accepts.
/// </summary>
public static class ClipboardAttachments
{
    /// <summary>
    /// Anthropic resizes anything larger than this before the model sees it, and
    /// bills for the full upload either way, so shrink first. Screenshots from a
    /// 4K monitor are several megabytes; base64 adds another third on top.
    /// </summary>
    private const int MaxEdge = 1568;

    /// <summary>
    /// Reads everything on the clipboard that can travel with a message, in the
    /// order it was copied. Images come back decoded, so they can go inline;
    /// everything else comes back as a path for the CLI to open itself. Empty
    /// when there is nothing to attach.
    ///
    /// Handles a bitmap (a screenshot, or a copy out of an image editor) as well
    /// as anything copied in File Explorer, one file or a whole selection.
    /// </summary>
    public static IReadOnlyList<IChatAttachment> TryRead()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var attachments = new List<IChatAttachment>();
                foreach (var path in Clipboard.GetFileDropList())
                {
                    var attachment = TryReadPath(path);
                    if (attachment is not null) attachments.Add(attachment);
                }
                if (attachments.Count > 0) return attachments;
            }

            if (Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is not null)
                    return new IChatAttachment[] { FromBitmapSource(source) };
            }
        }
        catch
        {
            // The clipboard is shared with every other process on the machine and
            // can fail for reasons that have nothing to do with us. Pasting
            // nothing is the right answer.
        }

        return Array.Empty<IChatAttachment>();
    }

    private static IChatAttachment? TryReadPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Directory.Exists(path)) return new ChatFileAttachment(path!);
        if (!File.Exists(path)) return null;

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
    /// Decodes a copied image file, or returns null when the extension is not one
    /// the API takes inline — those go out as paths instead.
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
            return new ChatImageAttachment(File.ReadAllBytes(path), mediaType);

        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.UriSource = new Uri(path);
        decoded.EndInit();
        return FromBitmapSource(decoded);
    }

    private static ChatImageAttachment FromBitmapSource(BitmapSource source)
    {
        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource final = scale < 1.0
            ? new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale))
            : source;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ChatImageAttachment(stream.ToArray(), "image/png");
    }
}
