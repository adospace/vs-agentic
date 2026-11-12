using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.ViewModels.Banners;

/// <summary>One part of the plan, with a model the user can change.</summary>
public partial class SplitTaskRowViewModel : ObservableObject
{
    public SplitTaskRowViewModel(AgentSplitTask task)
    {
        Role = task.Role;
        AgentType = task.AgentType;
        Task = task.Task;
        Stage = task.Stage;
        Writes = task.Writes;
        _selectedModel = SplitPlanBannerViewModel.Models.Contains(task.Model) ? task.Model : "sonnet";
    }

    public string Role { get; }
    public string AgentType { get; }
    public string Task { get; }
    public int Stage { get; }
    public bool Writes { get; }

    public string Details => $"{AgentType} · stage {Stage} · {(Writes ? "changes files" : "read-only")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHaiku), nameof(IsSonnet), nameof(IsOpus), nameof(Warning), nameof(HasWarning))]
    private string _selectedModel;

    /// <summary>Why the chosen model is risky for this part, or null.</summary>
    public string? Warning => VsAgentic.Services.Agents.AgentSplitText.ModelWarning(SelectedModel, Writes);
    public bool HasWarning => Warning is not null;

    // One radio button per model; WPF groups the three in each row by their panel.
    public bool IsHaiku { get => SelectedModel == "haiku"; set { if (value) SelectedModel = "haiku"; } }
    public bool IsSonnet { get => SelectedModel == "sonnet"; set { if (value) SelectedModel = "sonnet"; } }
    public bool IsOpus { get => SelectedModel == "opus"; set { if (value) SelectedModel = "opus"; } }

    public AgentSplitTask ToTask() => new AgentSplitTask
    {
        Role = Role, AgentType = AgentType, Model = SelectedModel, Task = Task, Stage = Stage, Writes = Writes
    };
}

/// <summary>
/// The card above the message box that shows a split plan while Claude makes
/// it, then the parts with a model each, and runs it or drops it.
/// </summary>
public partial class SplitPlanBannerViewModel : ObservableObject, IBannerViewModel
{
    public static readonly IReadOnlyList<string> Models = new[] { "haiku", "sonnet", "opus" };

    private readonly Action<IReadOnlyList<AgentSplitTask>> _onRun;
    private readonly Action _onAlone;
    private readonly Action _onCancel;

    private Action? _onPlanWithHistory;

    /// <summary>
    /// Says the plan was made from the task alone, and offers to plan again
    /// in a copy of the chat. Null hides both.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryNote))]
    private string? _historyNote;

    public bool HasHistoryNote => HistoryNote is not null && !IsPlanning;

    /// <summary>The plan was made without the chat; the user can ask for one with it.</summary>
    public void OfferChatHistory(string note, Action planWithHistory)
    {
        _onPlanWithHistory = planWithHistory;
        HistoryNote = note;
    }

    /// <summary>Back to "planning…", for a second plan on the same card.</summary>
    public void Restart(string title)
    {
        HistoryNote = null;
        Rows.Clear();
        Reason = null;
        Footnote = null;
        ErrorText = null;
        Title = title;
        IsPlanning = true;
        OnPropertyChanged(nameof(PreferAlone)); OnPropertyChanged(nameof(PreferAgents));
    }

    [RelayCommand]
    private void PlanWithHistory() => _onPlanWithHistory?.Invoke();

    partial void OnIsPlanningChanged(bool value) => OnPropertyChanged(nameof(HasHistoryNote));

    public SplitPlanBannerViewModel(string task, string orchestratorName,
        Action<IReadOnlyList<AgentSplitTask>> onRun, Action onAlone, Action onCancel)
    {
        TaskText = task;
        OrchestratorText = $"orchestrator · {orchestratorName} · plans, hands out the parts, checks the results";
        _onRun = onRun;
        _onAlone = onAlone;
        _onCancel = onCancel;
    }

    /// <summary>Claude thinks splitting pays off; "Run with agents" is the main button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreferAlone))]
    private bool _worthSplitting = true;

    /// <summary>Claude suggests one agent; "Continue alone" is the main button.</summary>
    public bool PreferAlone => IsReady && !WorthSplitting || HasError;
    public bool PreferAgents => !PreferAlone;

    public IReadOnlyList<string> ModelChoices => Models;
    public string TaskText { get; }
    public string OrchestratorText { get; }
    public ObservableCollection<SplitTaskRowViewModel> Rows { get; } = new ObservableCollection<SplitTaskRowViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isPlanning = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string? _errorText;

    [ObservableProperty]
    private string _title = "Planning how to split this across agents…";

    [ObservableProperty]
    private string? _reason;

    [ObservableProperty]
    private string? _footnote;

    public bool IsReady => !IsPlanning && ErrorText is null;
    public bool HasError => ErrorText is not null;

    public void ShowPlan(AgentSplitPlan plan)
    {
        Rows.Clear();
        foreach (var t in plan.Tasks.OrderBy(t => t.Stage))
            Rows.Add(new SplitTaskRowViewModel(t));

        var stages = plan.Tasks.Select(t => t.Stage).Distinct().Count();
        var shape = $"{plan.Tasks.Count} {(plan.Tasks.Count == 1 ? "part" : "parts")} in {stages} {(stages == 1 ? "stage" : "stages")}";
        Title = plan.WorthSplitting
            ? "Plan: " + shape
            : plan.Tasks.Count == 0
                ? "Claude suggests one agent for this task"
                : "Claude suggests one agent for this task · the split would be " + shape;
        Reason = plan.Reason;
        WorthSplitting = plan.WorthSplitting;
        Footnote = plan.PlanningCostUsd is decimal c
            ? "Making this plan on Sonnet cost $" + c.ToString("0.00", CultureInfo.InvariantCulture) + ". Change any model before you run it."
            : "Change any model before you run it.";
        IsPlanning = false;
        OnPropertyChanged(nameof(PreferAlone)); OnPropertyChanged(nameof(PreferAgents));
    }

    public void ShowError(string message)
    {
        Title = "Could not make a plan";
        ErrorText = message;
        IsPlanning = false;
        OnPropertyChanged(nameof(PreferAlone)); OnPropertyChanged(nameof(PreferAgents));
    }

    /// <summary>Sends the task as an ordinary message, without a split.</summary>
    [RelayCommand]
    private void Alone() => _onAlone();

    private bool CanRun() => IsReady && Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run() => _onRun(Rows.Select(r => r.ToTask()).ToList());

    [RelayCommand]
    private void Cancel() => _onCancel();
}
