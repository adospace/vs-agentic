using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// One row of the background tasks strip: what runs, a Stop button, and for a
/// shell command the end of its output in the tooltip.
/// </summary>
public partial class BackgroundTaskItemViewModel : ObservableObject
{
    // A tooltip has room for a screenful, not for the 8 KiB the CLI returns.
    private const int OutputLines = 20;

    private readonly IChatService _chatService;
    private readonly ILogger _logger;

    public BackgroundTaskItemViewModel(BackgroundTask task, IChatService chatService, ILogger logger)
    {
        Id = task.Id;
        Description = task.Description;
        IsShell = task.IsShell;
        _chatService = chatService;
        _logger = logger;
        _toolTipText = task.Description;
    }

    public string Id { get; }

    public string Description { get; }

    public bool IsShell { get; }

    /// <summary>
    /// The description, and for a shell command the last lines it wrote, as
    /// of the last time the tooltip opened.
    /// </summary>
    [ObservableProperty]
    private string _toolTipText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isStopping;

    /// <summary>
    /// Asks the CLI to stop the task. The row goes when the CLI reports the
    /// task gone; until then the button stays off.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsStopping = true;
        bool sent;
        try { sent = await _chatService.StopBackgroundTaskAsync(Id); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VM] Could not stop background task {Id}", Id);
            sent = false;
        }
        if (!sent) IsStopping = false;
    }

    private bool CanStop() => !IsStopping;

    /// <summary>
    /// Reads the end of the output again. Called as the tooltip opens, so the
    /// output is as fresh as the moment the user looks.
    /// </summary>
    public async Task RefreshOutputAsync()
    {
        if (!IsShell) return;

        BackgroundTaskOutput? output;
        try { output = await _chatService.GetBackgroundTaskOutputAsync(Id); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VM] Could not read the output of background task {Id}", Id);
            return;
        }
        if (output is null) return;

        ToolTipText = Description + "\n\n" + Tail(output);
    }

    private static string Tail(BackgroundTaskOutput output)
    {
        var lines = output.Output.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 1 && lines[0].Length == 0)
            return "(no output yet)";

        var cut = output.Truncated || lines.Length > OutputLines;
        var shown = lines.Length > OutputLines ? lines.Skip(lines.Length - OutputLines) : lines;
        return (cut ? "…\n" : "") + string.Join("\n", shown);
    }
}
