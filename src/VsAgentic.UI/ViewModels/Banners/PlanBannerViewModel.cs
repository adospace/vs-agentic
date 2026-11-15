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

    public PlanBannerViewModel(PermissionRequest request, Action<PermissionDecision> onResolved)
        : base(request, onResolved)
    {
        _request = request;
    }

    /// <summary>Shows the feedback box in place of the answer buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing))]
    private bool _isGivingFeedback;

    public bool IsChoosing => !IsGivingFeedback;

    [ObservableProperty]
    private string _feedbackText = "";

    // The answer commands use the base's CanDecide: answered once, and held
    // for a moment when the plan comes up after another prompt (#176).

    /// <summary>Start, and let Claude edit files without asking.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void StartAcceptingEdits() => Resolve(PermissionDecision.AllowAndSetMode(InputJson(), AcceptEditsMode));

    /// <summary>
    /// Start, and ask before each edit. The mode is set explicitly, so the
    /// session asks whatever mode it was in before plan mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void StartAskingBeforeEdits() => Resolve(PermissionDecision.AllowAndSetMode(InputJson(), AskBeforeEditsMode));

    /// <summary>Only opens the feedback box, but held like the answers, so
    /// the second click of a double-click does not open it unread.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void KeepPlanning() => IsGivingFeedback = true;

    [RelayCommand]
    private void BackToChoices() => IsGivingFeedback = false;

    /// <summary>
    /// Sends the plan back. Wire-level a deny: the CLI stays in plan mode and
    /// hands the message to Claude as the result of its call, which is what
    /// makes it revise the plan. Measured on CLI 2.1.316.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void SendFeedback()
    {
        Resolve(PermissionDecision.Deny(PlanAnswers.KeepPlanning(FeedbackText)));
    }

    // Called by the base when the banner is answered and when the input hold
    // starts or ends.
    protected override void NotifyDecisionCommands()
    {
        base.NotifyDecisionCommands();
        StartAcceptingEditsCommand.NotifyCanExecuteChanged();
        StartAskingBeforeEditsCommand.NotifyCanExecuteChanged();
        KeepPlanningCommand.NotifyCanExecuteChanged();
        SendFeedbackCommand.NotifyCanExecuteChanged();
    }

    private string InputJson() =>
        _request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : _request.Input.GetRawText();
}
