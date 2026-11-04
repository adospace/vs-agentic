using CommunityToolkit.Mvvm.ComponentModel;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// A message sent while a turn runs. It waits in the CLI until a turn reads
/// it, and stays in the strip above the input box until then. Each one keeps
/// its own text and attachments, so a message taken back returns exactly as
/// it was sent.
/// </summary>
public partial class QueuedMessageViewModel : ObservableObject
{
    public QueuedMessageViewModel(
        string text,
        string message,
        List<ChatImageAttachment>? images,
        IReadOnlyList<IChatAttachment> attachments)
    {
        Id = Guid.NewGuid().ToString();
        Text = text;
        Message = message;
        Images = images;
        Attachments = attachments;
    }

    /// <summary>The uuid the message carries in the CLI.</summary>
    public string Id { get; }

    /// <summary>What the user typed.</summary>
    public string Text { get; }

    /// <summary>What goes to the CLI: the typed text with the attached paths.</summary>
    public string Message { get; }

    public List<ChatImageAttachment>? Images { get; }

    /// <summary>The attachments as they were in the input box.</summary>
    public IReadOnlyList<IChatAttachment> Attachments { get; }

    public string DisplayText =>
        Text.Length > 0 ? Text.Replace("\r", "").Replace('\n', ' ') :
        Attachments.Count == 1 ? "1 attachment" :
        $"{Attachments.Count} attachments";

    /// <summary>Set when a turn has read the message.</summary>
    public bool Started { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isCancelling;

    public bool CanCancel => !IsCancelling;
}
