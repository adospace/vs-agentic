using System.Collections.Generic;
using System.Linq;
using System.Text;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Diagnostics;

/// <summary>
/// The result of the setup check, grouped in sections. Rendered as Markdown
/// for the chat and as plain text for the dialog in Tools → Options; both
/// are meant to be pasted into a bug report as they are.
/// </summary>
public sealed class SetupCheckReport
{
    public const string Title = "Setup check";

    private const string Footer =
        "Nothing was changed. For Claude Code's own check, run `claude doctor` in a terminal.";

    /// <summary>
    /// The fixes offered, one per kind, in the order of their lines. Install
    /// comes before sign-in: signing in needs the CLI.
    /// </summary>
    public IReadOnlyList<SetupFix> Fixes => Items
        .Where(i => i.Fix is not null)
        .Select(i => i.Fix!)
        .GroupBy(f => f.Kind)
        .Select(g => g.First())
        .ToList();

    public SetupCheckReport(string? environment, IReadOnlyList<SetupCheckSection> sections)
    {
        Environment = environment;
        Sections = sections;
    }

    /// <summary>Versions of the extension, Visual Studio and Windows, on one line.</summary>
    public string? Environment { get; }

    public IReadOnlyList<SetupCheckSection> Sections { get; }

    public IEnumerable<SetupCheckItem> Items => Sections.SelectMany(s => s.Items);

    public int ErrorCount => Items.Count(i => i.Status == SetupCheckStatus.Error);

    public int WarningCount => Items.Count(i => i.Status == SetupCheckStatus.Warning);

    /// <summary>One line on top: everything fine, or how many lines need attention.</summary>
    public string Summary
    {
        get
        {
            var errors = ErrorCount;
            var warnings = WarningCount;
            if (errors == 0 && warnings == 0) return "Everything VsAgentic needs is in place.";

            var parts = new List<string>();
            if (errors > 0) parts.Add(errors == 1 ? "1 problem" : $"{errors} problems");
            if (warnings > 0) parts.Add(warnings == 1 ? "1 warning" : $"{warnings} warnings");
            return string.Join(" and ", parts) + " found.";
        }
    }

    public static string Symbol(SetupCheckStatus status) => status switch
    {
        SetupCheckStatus.Ok => "✅",
        SetupCheckStatus.Warning => "⚠️",
        SetupCheckStatus.Error => "❌",
        _ => "ℹ️",
    };

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(Title).Append("** — ").Append(Summary).Append("\n\n");

        foreach (var section in Sections.Where(s => s.Items.Count > 0))
        {
            sb.Append("**").Append(section.Name).Append("**\n\n");
            foreach (var item in section.Items)
            {
                sb.Append("- ").Append(Symbol(item.Status)).Append(' ')
                  .Append("**").Append(item.Title).Append("**: ")
                  .Append(item.Detail).Append('\n');
            }
            sb.Append('\n');
        }

        if (!string.IsNullOrEmpty(Environment))
            sb.Append("_").Append(Environment).Append("_\n\n");
        sb.Append(Footer);
        return sb.ToString();
    }

    /// <summary>
    /// The same report without Markdown: code spans lose their backticks, and
    /// a plain text box shows the rest as it is.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        sb.Append(Title).Append(" — ").Append(Summary).Append("\r\n\r\n");

        foreach (var section in Sections.Where(s => s.Items.Count > 0))
        {
            sb.Append(section.Name).Append("\r\n");
            foreach (var item in section.Items)
            {
                sb.Append("  ").Append(Symbol(item.Status)).Append(' ')
                  .Append(item.Title).Append(": ")
                  .Append(StripMarkdown(item.Detail)).Append("\r\n");
            }
            sb.Append("\r\n");
        }

        if (!string.IsNullOrEmpty(Environment))
            sb.Append(Environment).Append("\r\n\r\n");
        sb.Append(StripMarkdown(Footer));
        return sb.ToString();
    }

    private static string StripMarkdown(string text) => text.Replace("`", "");
}

/// <summary>A group of lines under a heading ("Claude Code", "This chat" …).</summary>
public sealed record SetupCheckSection(string Name, IReadOnlyList<SetupCheckItem> Items);
