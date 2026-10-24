using System;
using System.Collections.Generic;
using System.Linq;
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
    /// CLI has answered, in this session or another.
    /// </summary>
    public IReadOnlyList<SlashCommandOption> SlashCommands => _slashCommands;

    private void InitializeSlashCommands()
    {
        ClaudeCommandCatalog.ReportedChanged += OnReportedCommandsChanged;
        RebuildSlashCommands(ClaudeCommandCatalog.Reported);
    }

    private void OnReportedCommandsChanged(IReadOnlyList<ClaudeCommandInfo> commands) =>
        Dispatch(() => RebuildSlashCommands(commands));

    private void RebuildSlashCommands(IReadOnlyList<ClaudeCommandInfo>? commands)
    {
        // Stable sort: within each group the CLI's own order stays.
        _slashCommands = (commands ?? Array.Empty<ClaudeCommandInfo>())
            .Select(c => new SlashCommandOption(c))
            .OrderBy(c => c.Group == SlashCommandOption.CommandsGroup ? 0 : 1)
            .ToList();
        OnPropertyChanged(nameof(SlashCommands));
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
