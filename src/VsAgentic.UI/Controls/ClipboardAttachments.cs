using System.Windows;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Decides which of the formats the clipboard is holding becomes an attachment.
/// Reading a path or a bitmap is <see cref="AttachmentReader"/>'s job; this
/// class only knows what the user meant by pressing Ctrl+V.
/// </summary>
public static class ClipboardAttachments
{
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
            // Files come first and win over text: copying them in Explorer is
            // deliberate, and the text some apps publish next to them is the
            // path we are attaching anyway.
            if (Clipboard.ContainsFileDropList())
            {
                var attachments = AttachmentReader.FromPaths(Clipboard.GetFileDropList().Cast<string?>());
                if (attachments.Count > 0) return attachments;
            }

            // A bitmap loses to text. Excel, Word and some browsers publish a
            // picture of the selection next to the text they copy, and taking
            // the picture would paste an image of the cells the user meant to
            // paste as text. A screenshot carries no text format, so it still
            // attaches.
            if (!Clipboard.ContainsText() && Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is not null)
                    return new IChatAttachment[] { AttachmentReader.FromBitmap(source) };
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
}
