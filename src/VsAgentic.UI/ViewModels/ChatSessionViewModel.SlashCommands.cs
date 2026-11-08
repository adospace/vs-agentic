using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using VsAgentic.Services.Diagnostics;

namespace VsAgentic.UI.ViewModels;

/// <summary>One row of the menu under "/" in the chat input.</summary>
public sealed class SlashCommandOption
{
    // The CLI's builtin flag marks everything that ships with Claude Code,
    // its bundled skills (dataviz, verify …) as much as its commands; it is
    // absent for plugin, user and project skills. There is no field that
    // tells a command from a skill, so the groups say where a row comes from.
    public const string CommandsGroup = "Claude Code";
    public const string SkillsGroup = "Plugins and your own";

    // Commands that change what the model panel shows. Typed in the input,
    // the CLI would switch behind the panel's back; the menu opens the panel
    // instead.
    private static readonly HashSet<string> PanelCommands =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "model", "effort", "fast" };

    /// <summary>The setup check; typed without arguments it runs in VsAgentic.</summary>
    public const string SetupCheckName = "doctor";

    internal const string SetupCheckDescription =
        "Checks that VsAgentic and Claude Code are set up. Nothing is sent to Claude.";

    public SlashCommandOption(ClaudeCommandInfo info)
    {
        Name = info.Name;
        ArgumentHint = string.IsNullOrWhiteSpace(info.ArgumentHint) ? null : info.ArgumentHint;
        Group = info.IsBuiltin ? CommandsGroup : SkillsGroup;
        OpensModelPanel = info.IsBuiltin && PanelCommands.Contains(info.Name);
        var isSetupCheck = info.IsBuiltin && string.Equals(info.Name, SetupCheckName, StringComparison.OrdinalIgnoreCase);
        Description = OpensModelPanel
            ? "Opens the model panel (model, reasoning effort, Fast mode)"
            : isSetupCheck ? SetupCheckDescription : info.Description;
        if (isSetupCheck) ArgumentHint = null;
    }

    /// <summary>What follows the slash.</summary>
    public string Name { get; }

    public string Display => "/" + Name;

    public string? ArgumentHint { get; }

    public string? Description { get; }

    /// <summary>"Claude Code" for what ships with it, "Plugins and your own" for the rest.</summary>
    public string Group { get; }

    /// <summary>Picking it opens the model panel rather than inserting the command.</summary>
    public bool OpensModelPanel { get; }
}

public partial class ChatSessionViewModel
{
    private IReadOnlyList<SlashCommandOption> _slashCommands = Array.Empty<SlashCommandOption>();

    /// <summary>
    /// Every command the CLI reported, Claude Code's own first. Empty until a
    /// CLI has answered, in this session or another one in the same folder.
    /// </summary>
    public IReadOnlyList<SlashCommandOption> SlashCommands => _slashCommands;

    /// <summary>
    /// False until a CLI has listed its commands. <see cref="SlashCommands"/>
    /// then holds only the setup check.
    /// </summary>
    public bool HasReportedSlashCommands { get; private set; }

    private void InitializeSlashCommands()
    {
        ClaudeCommandCatalog.ReportedChanged += OnReportedCommandsChanged;
        RebuildSlashCommands();
        SubscribeSetupFixes();
    }

    // Another folder's list holds that project's skills, not ours.
    private void OnReportedCommandsChanged(string workingDirectory)
    {
        if (ClaudeCommandCatalog.SameDirectory(workingDirectory, WorkingDirectory))
            Dispatch(RebuildSlashCommands);
    }

    private void RebuildSlashCommands()
    {
        // Read here, not taken from the event: of two reports that race, the
        // catalog holds the one that came last.
        var commands = ClaudeCommandCatalog.Reported(WorkingDirectory);
        HasReportedSlashCommands = commands is { Count: > 0 };

        // The setup check is there before the CLI has answered, and when it
        // cannot: that is when it is needed.
        var all = (commands ?? Array.Empty<ClaudeCommandInfo>()).ToList();
        if (!all.Any(c => c.IsBuiltin && string.Equals(c.Name, SlashCommandOption.SetupCheckName, StringComparison.OrdinalIgnoreCase)))
            all.Insert(0, new ClaudeCommandInfo(SlashCommandOption.SetupCheckName, null, null, IsBuiltin: true));

        // Stable sort: within each group the CLI's own order stays.
        _slashCommands = all
            .Select(c => new SlashCommandOption(c))
            .OrderBy(c => c.Group == SlashCommandOption.CommandsGroup ? 0 : 1)
            .ToList();
        OnPropertyChanged(nameof(SlashCommands));
    }

    // ── /model, /effort and /fast typed in full ───────────────────────────
    // Sent to the CLI, they would switch it behind the panel's back. They go
    // through the panel's own state instead, as a pick in the panel does.

    private static readonly Regex PanelCommandRe = new Regex(
        @"^/(model|effort|fast)(?:\s+(\S+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Opens the model panel; false when the view cannot show it (its button
    /// is hidden in a narrow window). Set by the view.
    /// </summary>
    public Func<bool>? OpenModelPanel { get; set; }

    /// <summary>
    /// Handles a message that is only "/model", "/effort" or "/fast", with or
    /// without one argument. Without one the panel opens. "/model opus",
    /// "/effort high" and "/fast on" set the panel, which tells the CLI. False
    /// when the message is something else or the argument is not one the panel
    /// knows; the message then goes to the CLI as before.
    /// </summary>
    private bool TryRunPanelCommand(string message)
    {
        var match = PanelCommandRe.Match(message);
        if (!match.Success) return false;

        var arg = match.Groups[2].Success ? match.Groups[2].Value : null;
        if (arg is null) return OpenModelPanel?.Invoke() == true;

        switch (match.Groups[1].Value.ToLowerInvariant())
        {
            case "model":
                // The panel's Default row has no alias; the CLI calls it "default".
                if (string.Equals(arg, "default", StringComparison.OrdinalIgnoreCase))
                {
                    SelectedModel = OptionFor("");
                    return true;
                }
                // A model the panel lists, or a full id. For any other name
                // OptionFor would add a row, as it does for a typo in the Model
                // setting, and the next message would fail; the CLI answers a
                // name it does not know.
                if (!_modelOptions.Any(m => string.Equals(m.Alias, arg, StringComparison.OrdinalIgnoreCase))
                    && !arg.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
                    return false;
                SelectedModel = OptionFor(arg);
                return true;

            case "effort":
                if (char.IsDigit(arg[0]) || !Enum.TryParse<ClaudeEffort>(arg, ignoreCase: true, out var effort)
                    || !Enum.IsDefined(typeof(ClaudeEffort), effort))
                    return false;
                SelectedEffort = effort;
                return true;

            case "fast":
                bool on;
                if (arg.Equals("on", StringComparison.OrdinalIgnoreCase)) on = true;
                else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) on = false;
                else return false;
                if (!CanToggleFastMode || (on && SelectedModel?.SupportsFastMode != true)) return false;
                IsFastModeOn = on;
                return true;
        }
        return false;
    }

    // ── /doctor ───────────────────────────────────────────────────────────
    // Typed alone it runs VsAgentic's setup check, which only reads and sends
    // nothing to Claude. With an argument it goes to the CLI, whose /doctor
    // in a session like ours is a skill that the model runs.

    private static readonly Regex SetupCheckRe = new Regex(
        @"^/" + SlashCommandOption.SetupCheckName + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs the setup check; given this chat's service when it can describe
    /// its session. Set by the host, which knows the options and Visual Studio.
    /// </summary>
    public Func<ISessionSetupInfo?, Task<SetupCheckReport>>? RunSetupCheck { get; set; }

    // The report's heading: who wrote it, and that Claude did not see it.
    private const string SetupCheckLabel = "VSAGENTIC · NOT SENT TO CLAUDE";

    private bool TryStartSetupCheck(string message)
    {
        if (RunSetupCheck is null || !SetupCheckRe.IsMatch(message)) return false;
        _ = RunSetupCheckInChatAsync(RunSetupCheck);
        return true;
    }

    /// <summary>
    /// Shows the report as a row in the chat that is not part of the
    /// conversation: not sent, not stored with the session.
    /// </summary>
    private async Task RunSetupCheckInChatAsync(Func<ISessionSetupInfo?, Task<SetupCheckReport>> run)
    {
        var (id, item) = AddNotice("_Checking the setup…_", "setup-check", SetupCheckLabel);
        await FillSetupCheckAsync(id, item, run, note: null);
    }

    /// <summary>Runs the check and puts its report and fixes in the row.</summary>
    private async Task FillSetupCheckAsync(
        string id, ChatItemViewModel item, Func<ISessionSetupInfo?, Task<SetupCheckReport>> run, string? note)
    {
        string content;
        SetupCheckReport? report = null;
        try
        {
            report = await run(_chatService as ISessionSetupInfo);
            content = report.ToMarkdown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Setup check failed");
            content = $"**{SetupCheckReport.Title}** could not finish: {ex.Message}";
        }

        if (!string.IsNullOrEmpty(note)) content = note + "\n\n" + content;

        _setupChecks[id] = new SetupCheckRow(item, report);
        item.Content = content;
        MessageContentUpdated?.Invoke(id, content);
        NoticeActionsChanged?.Invoke(id, SetupCheckActions(report, enabled: true));
        RequestScroll();
    }

    // ── Setup fixes ───────────────────────────────────────────────────────
    // A line of the report that VsAgentic can fix gets a button under the
    // report. A fix that runs a command does so in a terminal window the user
    // sees; the check runs again in the same row when the fix is done.

    private sealed record SetupCheckRow(ChatItemViewModel Item, SetupCheckReport? Report);

    // The rows with a setup check report, by row id. Lives as long as the chat.
    private readonly Dictionary<string, SetupCheckRow> _setupChecks = new();

    private const string RecheckActionId = "recheck";

    // Offered once per chat when a message finds no CLI; /doctor still works.
    private bool _setupCheckOffered;

    /// <summary>
    /// Runs a fix and returns a sentence for the top of the next report, or
    /// null. Set by the host, which can change the options and open windows.
    /// </summary>
    public Func<SetupFix, Task<string?>>? ApplySetupFix { get; set; }

    /// <summary>
    /// Raised with the buttons a row VsAgentic wrote should now have. The
    /// view passes them to the chat page.
    /// </summary>
    public event Action<string, IReadOnlyList<ChatNoticeAction>>? NoticeActionsChanged;

    private static IReadOnlyList<ChatNoticeAction> SetupCheckActions(SetupCheckReport? report, bool enabled)
    {
        var actions = new List<ChatNoticeAction>();
        if (report is not null)
        {
            // Without WebView2 there is no chat to show this button in; the
            // dialog in Tools → Options offers it.
            foreach (var fix in report.Fixes.Where(f => f.Kind != SetupFixKind.InstallWebView2))
                actions.Add(new ChatNoticeAction { Id = fix.Id, Label = fix.Label, Enabled = enabled });
        }
        actions.Add(new ChatNoticeAction { Id = RecheckActionId, Label = "Check again", Enabled = enabled });
        return actions;
    }

    private void SubscribeSetupFixes()
    {
        if (_chatService is ISessionSetupInfo session)
            session.CliNotFound += OnCliNotFound;
    }

    // A message found no CLI: show the check, which offers to install it.
    private void OnCliNotFound()
    {
        Dispatch(() =>
        {
            if (_setupCheckOffered || RunSetupCheck is null) return;
            _setupCheckOffered = true;
            _ = RunSetupCheckInChatAsync(RunSetupCheck);
        });
    }

    /// <summary>A button under a row VsAgentic wrote was clicked. UI thread.</summary>
    public void OnNoticeAction(string noticeId, string actionId)
    {
        if (!_setupChecks.TryGetValue(noticeId, out var row) || RunSetupCheck is not { } run) return;
        _ = RunSetupFixAsync(noticeId, row, actionId, run);
    }

    private async Task RunSetupFixAsync(
        string id, SetupCheckRow row, string actionId, Func<ISessionSetupInfo?, Task<SetupCheckReport>> run)
    {
        NoticeActionsChanged?.Invoke(id, SetupCheckActions(row.Report, enabled: false));

        string? note = null;
        var fix = row.Report?.Fixes.FirstOrDefault(f => f.Id == actionId);
        if (fix is not null && ApplySetupFix is { } apply)
        {
            var waiting = $"_{fix.Label}: {WaitingText(fix.Kind)}_";
            row.Item.Content = waiting;
            MessageContentUpdated?.Invoke(id, waiting);

            try
            {
                note = await apply(fix);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Setup fix {Fix} failed", fix.Kind);
                note = $"{fix.Label} did not run: {ex.Message}";
            }

            // The next message starts a CLI with the new path or sign-in.
            if (fix.Kind != SetupFixKind.InstallWebView2 && !IsBusy)
            {
                try { (_chatService as ISessionSetupInfo)?.RestartAfterSetupFix(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not restart the CLI after a setup fix"); }
            }
        }

        row.Item.Content = "_Checking the setup…_";
        MessageContentUpdated?.Invoke(id, row.Item.Content);
        await FillSetupCheckAsync(id, row.Item, run, note is null ? null : "**" + note + "**");
    }

    private static string WaitingText(SetupFixKind kind) => kind switch
    {
        SetupFixKind.UseFoundCli => "setting Claude CLI Path…",
        SetupFixKind.InstallWebView2 => "opening the download page…",
        _ => "follow the terminal window that opened. VsAgentic checks the setup again when you close it.",
    };

    /// <summary>
    /// The rows whose name holds what was typed after the slash, those that
    /// start with it first. Groups keep their order.
    /// </summary>
    public IReadOnlyList<SlashCommandOption> FilterSlashCommands(string typed)
    {
        if (string.IsNullOrEmpty(typed)) return _slashCommands;

        return _slashCommands
            .Where(c => c.Name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(c => c.Group == SlashCommandOption.CommandsGroup ? 0 : 1)
            .ThenBy(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}
