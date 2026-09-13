namespace VsAgentic.Services.Abstractions;

/// <summary>
/// Something the user attached to the next message. Images travel inline as
/// base64 blocks; every other file travels as a path the CLI opens for itself.
/// Both share one pending list so the strip above the input box keeps them in
/// the order they were pasted.
/// </summary>
public interface IChatAttachment
{
    /// <summary>Label for the attachment chip above the input box.</summary>
    string DisplayName { get; }
}
