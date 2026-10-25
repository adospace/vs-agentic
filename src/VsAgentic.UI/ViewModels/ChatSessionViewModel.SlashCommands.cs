using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VsAgentic.Services.Configuration;

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

    public SlashCommandOption(ClaudeCommandInfo info)
    {
        Name = info.Name;
        ArgumentHint = string.IsNullOrWhiteSpace(info.ArgumentHint) ? null : info.ArgumentHint;
        Group = info.IsBuiltin ? CommandsGroup : SkillsGroup;
        OpensModelPanel = info.IsBuiltin && PanelCommands.Contains(info.Name);
        Description = OpensModelPanel
            ? "Opens the model panel (model, reasoning effort, Fast mode)"
            : info.Description;
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

    private void InitializeSlashCommands()
    {
        ClaudeCommandCatalog.ReportedChanged += OnReportedCommandsChanged;
        RebuildSlashCommands();
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

        // Stable sort: within each group the CLI's own order stays.
        _slashCommands = (commands ?? Array.Empty<ClaudeCommandInfo>())
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
                SelectedModel = OptionFor(string.Equals(arg, "default", StringComparison.OrdinalIgnoreCase) ? "" : arg);
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
