using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Renders a pending <see cref="ChatImageAttachment"/> as a thumbnail. The bytes
/// are already in memory, so the preview is decoded from them rather than from
/// any file on disk.
/// </summary>
public sealed class AttachmentPreviewConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ChatImageAttachment attachment) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Decoding at thumbnail height keeps a wall of screenshots from
            // holding full-size bitmaps in memory.
            image.DecodePixelHeight = 56;
            image.StreamSource = new MemoryStream(attachment.Data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // A preview that cannot be decoded is not worth failing the paste
            // over — the attachment itself still goes out fine.
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
