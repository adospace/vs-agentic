using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.IdeContext;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The Review button beside the attach buttons. It sends a review request for
/// the uncommitted changes; Claude shows the findings in the Error List with
/// <see cref="ReviewTool.Name"/>. The button is only a shortcut: a review the
/// user asks for in their own words uses the same tool.
/// </summary>
public partial class ChatSessionViewModel
{
    /// <summary>False when the host has no Error List to show findings in.</summary>
    public bool ShowReviewChanges => IdeContext?.Find(ReviewTool.Name) is not null;

    private bool CanReviewChanges() => ShowReviewChanges && !IsBusy;

    /// <summary>
    /// Sends the review request. Text already in the message box goes with
    /// it, so the user can say what to look at, and so do the attachments.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReviewChanges))]
    private async Task ReviewChangesAsync()
    {
        var draft = InputText.Trim();
        InputText = draft.Length == 0
            ? ReviewTool.Request
            : ReviewTool.Request + "\n\nFocus: " + draft;

        if (SendCommand.CanExecute(null))
            await SendCommand.ExecuteAsync(null);
    }
}
