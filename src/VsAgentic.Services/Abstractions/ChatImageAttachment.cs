namespace VsAgentic.Services.Abstractions;

/// <summary>
/// An image travelling with a user message. The CLI accepts these inline as
/// base64 content blocks over stream-json stdin, so nothing has to be written
/// to the project first.
/// </summary>
public sealed class ChatImageAttachment : IChatAttachment
{
    /// <summary>Raw encoded bytes, already in <see cref="MediaType"/> format.</summary>
    public byte[] Data { get; }

    /// <summary>One of the media types the API accepts: PNG, JPEG, GIF or WebP.</summary>
    public string MediaType { get; }

    /// <summary>
    /// File name used when the attachment is persisted next to its session.
    /// Empty until the store assigns one.
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// Chips carry a thumbnail rather than a name, so this is only ever read as
    /// a tooltip or by accessibility tools.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(FileName) ? "Pasted image" : FileName;

    public ChatImageAttachment(byte[] data, string mediaType)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        MediaType = string.IsNullOrWhiteSpace(mediaType)
            ? throw new ArgumentException("Media type is required.", nameof(mediaType))
            : mediaType;
    }

    /// <summary>Media types the Claude API accepts for image blocks.</summary>
    public static readonly string[] SupportedMediaTypes =
        { "image/png", "image/jpeg", "image/gif", "image/webp" };

    public static bool IsSupported(string mediaType) =>
        Array.IndexOf(SupportedMediaTypes, mediaType) >= 0;

    /// <summary>Conventional file extension for <see cref="MediaType"/>.</summary>
    public string Extension => MediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".bin",
    };

    /// <summary>Data URI for rendering the image in the chat WebView.</summary>
    public string ToDataUri() => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
}
