using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.ClaudeCli.Questions;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>
/// View model for an AskUserQuestion card. Owns the per-question paging
/// state and the submit-or-next-unanswered behavior of the bottom button.
/// </summary>
public partial class QuestionCardViewModel : ObservableObject, IBannerViewModel
{
    private readonly UserQuestionRequest _request;
    private readonly Action<IReadOnlyDictionary<string, string>> _onSubmitted;

    public IReadOnlyList<QuestionViewModel> Questions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentQuestion))]
    [NotifyPropertyChangedFor(nameof(HeaderLabel))]
    [NotifyPropertyChangedFor(nameof(CounterText))]
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

    [RelayCommand]
    private void Submit()
    {
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
