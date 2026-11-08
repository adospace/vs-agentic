using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.ClaudeCli.Questions;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// View model for an AskUserQuestion card. Owns the per-question paging
/// state and the submit-or-next-unanswered behavior of the bottom button.
/// </summary>
public partial class QuestionCardViewModel : ObservableObject, IQueuedPrompt
{
    private readonly UserQuestionRequest _request;
    private readonly Action<IReadOnlyDictionary<string, string>> _onSubmitted;

    /// <summary>Broker id of the question this card stands for. Lets the chat
    /// match a cancellation back to the card on screen.</summary>
    public string ToolUseId => _request.ToolUseId;

    public IReadOnlyList<QuestionViewModel> Questions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentQuestion))]
    [NotifyPropertyChangedFor(nameof(HeaderLabel))]
    [NotifyPropertyChangedFor(nameof(CounterText))]
    [NotifyPropertyChangedFor(nameof(ShortcutHint))]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyCanExecuteChangedFor(nameof(GoPrevCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoNextCommand))]
    private int _currentIndex;

    public QuestionViewModel CurrentQuestion => Questions[CurrentIndex];

    public string HeaderLabel
    {
        get
        {
            var hdr = CurrentQuestion.Header;
            return string.IsNullOrEmpty(hdr) ? "Claude needs more information" : hdr;
        }
    }

    public string CounterText =>
        Questions.Count > 1 ? $"{CurrentIndex + 1} / {Questions.Count}" : "";

    public bool ShowNavigation => Questions.Count > 1;

    public bool CanGoPrev => CurrentIndex > 0;
    public bool CanGoNext => CurrentIndex < Questions.Count - 1;

    public bool IsAllAnswered => Questions.All(q => q.IsAnswered);

    public string SubmitLabel => IsAllAnswered ? "Submit" : "Next unanswered";

    /// <summary>
    /// The card's two keyboard shortcuts, spelled out next to the button.
    /// Alt+N only reaches the ninth option, and the range follows the question
    /// on screen rather than the longest one in the set.
    /// </summary>
    public string ShortcutHint
    {
        get
        {
            // The run covers the "Other" row too, which sits one past the
            // listed options.
            var count = Math.Min(CurrentQuestion.OtherOrdinal, 9);
            if (count < 1) return "Ctrl+Enter to submit";
            var keys = count == 1 ? "Alt+1" : $"Alt+1–{count}";
            return $"{keys} to choose, Ctrl+Enter to submit";
        }
    }

    public QuestionCardViewModel(
        UserQuestionRequest request,
        Action<IReadOnlyDictionary<string, string>> onSubmitted)
    {
        _request = request;
        _onSubmitted = onSubmitted;

        var list = new List<QuestionViewModel>(request.Questions.Count);
        foreach (var q in request.Questions)
        {
            var vm = new QuestionViewModel(q);
            vm.AnsweredChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(IsAllAnswered));
                OnPropertyChanged(nameof(SubmitLabel));
            };
            list.Add(vm);
        }
        Questions = list;
    }

    [RelayCommand(CanExecute = nameof(CanGoPrev))]
    private void GoPrev()
    {
        if (CurrentIndex > 0) CurrentIndex--;
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void GoNext()
    {
        if (CurrentIndex < Questions.Count - 1) CurrentIndex++;
    }

    // Set by the first submit. A second Ctrl+Enter or click must not answer
    // again: the next prompt in the queue would be taken down unread.
    private bool _submitted;

    /// <summary>True for a moment after the card comes up from the queue or
    /// right after another prompt was answered, while Submit stays disabled. See <see cref="HoldInput"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isInputHeld;

    /// <summary>
    /// Keeps Submit disabled for a moment. A card that replaces an answered
    /// prompt appears under the pointer and the keyboard focus, so a second
    /// click or a held key would otherwise answer it before it is read.
    /// </summary>
    public void HoldInput(TimeSpan time)
    {
        IsInputHeld = true;
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsInputHeld = false;
        };
        timer.Start();
    }

    private bool CanSubmit() => !_submitted && !IsInputHeld;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        if (!CanSubmit()) return;

        // If anything is still blank, jump to the next blank instead of
        // submitting — matches the prior builder's "Next unanswered" UX.
        var unanswered = FindFirstUnanswered(CurrentIndex);
        if (unanswered >= 0)
        {
            CurrentIndex = unanswered;
            return;
        }

        var answers = new Dictionary<string, string>();
        for (int i = 0; i < Questions.Count; i++)
            answers[_request.Questions[i].Question] = Questions[i].GetAnswer();
        _submitted = true;
        SubmitCommand.NotifyCanExecuteChanged();
        _onSubmitted(answers);
    }

    private int FindFirstUnanswered(int startAfter)
    {
        for (int i = startAfter + 1; i < Questions.Count; i++)
            if (!Questions[i].IsAnswered) return i;
        for (int i = 0; i <= startAfter && i < Questions.Count; i++)
            if (!Questions[i].IsAnswered) return i;
        return -1;
    }
}
