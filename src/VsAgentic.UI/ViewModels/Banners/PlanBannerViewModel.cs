using System;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.ClaudeCli.Permissions;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// The answer to a plan Claude put forward in plan mode (<c>ExitPlanMode</c>).
/// The plan itself is in the transcript, as the plan card above; this banner
/// only asks what to do with it: start with edits accepted, start and ask
/// before each edit, or keep planning with feedback.
///
/// A permission banner underneath, so a Stop or a request the CLI withdraws
/// takes it down like any other permission prompt.
/// </summary>
public partial class PlanBannerViewModel : PermissionBannerViewModel
{
    // CLI permission modes the plan can start in.
    private const string AcceptEditsMode = "acceptEdits";
    private const string AskBeforeEditsMode = "default";

    private readonly PermissionRequest _request;
    private readonly Action<PermissionDecision> _onResolved;

    // Set by the first answer. A second click must not answer again.
    private bool _resolved;

    public PlanBannerViewModel(PermissionRequest request, Action<PermissionDecision> onResolved)
        : base(request, onResolved)
    {
        _request = request;
        _onResolved = onResolved;
    }

    /// <summary>Shows the feedback box in place of the answer buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing))]
    private bool _isGivingFeedback;

    public bool IsChoosing => !IsGivingFeedback;

    [ObservableProperty]
    private string _feedbackText = "";

    private bool CanAnswer() => !_resolved;

    /// <summary>Start, and let Claude edit files without asking.</summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void StartAcceptingEdits() => Answer(PermissionDecision.AllowAndSetMode(InputJson(), AcceptEditsMode));

    /// <summary>
    /// Start, and ask before each edit. The mode is set explicitly, so the
    /// session asks whatever mode it was in before plan mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void StartAskingBeforeEdits() => Answer(PermissionDecision.AllowAndSetMode(InputJson(), AskBeforeEditsMode));

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void KeepPlanning() => IsGivingFeedback = true;

    [RelayCommand]
    private void BackToChoices() => IsGivingFeedback = false;

    /// <summary>
    /// Sends the plan back. Wire-level a deny: the CLI stays in plan mode and
    /// hands the message to Claude as the result of its call, which is what
    /// makes it revise the plan. Measured on CLI 2.1.316.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void SendFeedback()
    {
        Answer(PermissionDecision.Deny(PlanAnswers.KeepPlanning(FeedbackText)));
    }

    private void Answer(PermissionDecision decision)
    {
        if (!CanAnswer()) return;
        _resolved = true;
        NotifyAnswerCommands();
        _onResolved(decision);
    }

    private void NotifyAnswerCommands()
    {
        StartAcceptingEditsCommand.NotifyCanExecuteChanged();
        StartAskingBeforeEditsCommand.NotifyCanExecuteChanged();
        KeepPlanningCommand.NotifyCanExecuteChanged();
        SendFeedbackCommand.NotifyCanExecuteChanged();
    }

    private string InputJson() =>
        _request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : _request.Input.GetRawText();
}
