using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Turns whatever the clipboard is holding into an attachment the CLI accepts.
/// </summary>
public static class ClipboardImage
{
    /// <summary>
    /// Anthropic resizes anything larger than this before the model sees it, and
    /// bills for the full upload either way, so shrink first. Screenshots from a
    /// 4K monitor are several megabytes; base64 adds another third on top.
    /// </summary>
    private const int MaxEdge = 1568;

    /// <summary>
    /// Reads an image from the clipboard, or returns null when there is none.
    /// Handles both a bitmap (a screenshot, or a copy out of an image editor)
    /// and a copied image file.
    /// </summary>
    public static ChatImageAttachment? TryRead()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList())
                {
                    var attachment = TryReadFile(path);
                    if (attachment is not null) return attachment;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is not null)
                    return FromBitmapSource(source);
            }
        }
        catch
        {
            // The clipboard is shared with every other process on the machine and
            // can fail for reasons that have nothing to do with us. Pasting
            // nothing is the right answer.
        }

        return null;
    }

    private static ChatImageAttachment? TryReadFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

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
            return new ChatImageAttachment(File.ReadAllBytes(path!), mediaType);

        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.UriSource = new Uri(path!);
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
