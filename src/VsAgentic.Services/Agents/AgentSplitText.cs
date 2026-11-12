using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Agents;

/// <summary>
/// The message that runs a split plan, and the signs that a task would be
/// worth splitting. Plain text in, plain text out: the main conversation does
/// the work with its own Agent tool, so nothing here depends on one agent.
/// </summary>
public static class AgentSplitText
{
    /// <summary>
    /// The message the chat sends when the user runs a plan. Visible in the
    /// chat as the user's message, so it reads as an instruction a person
    /// could have written.
    /// </summary>
    public static string RunMessage(string task, IReadOnlyList<AgentSplitTask> tasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Split this task across subagents, as planned below. You coordinate and check the results; leave the work itself to the subagents.");
        sb.AppendLine();
        sb.AppendLine("Task: " + OneLine(task, 400));
        sb.AppendLine();

        foreach (var stage in tasks.GroupBy(t => t.Stage).OrderBy(g => g.Key))
        {
            var parts = stage.ToList();
            sb.AppendLine(parts.Count > 1
                ? $"Stage {stage.Key} — start these {parts.Count} Agent calls in ONE message, so they run at the same time:"
                : $"Stage {stage.Key}:");
            foreach (var t in parts)
            {
                sb.Append("- ").Append(t.Role)
                  .Append(": Agent tool with subagent_type \"").Append(t.AgentType)
                  .Append("\" and model \"").Append(t.Model).Append('"')
                  .Append(t.Writes ? "" : ", read-only")
                  .Append(". Prompt: ").AppendLine(OneLine(t.Task, 1200));
            }
            sb.AppendLine();
        }

        sb.Append("Wait for each stage before the next. Pass on what a stage found when the next one needs it. ")
          .Append("Use exactly these subagent_type and model values. ")
          // Verify first, then escalate: a cheap model with a check and a
          // retry one tier up beats running everything on the big one.
          .Append("Where a part can be checked (a build, a test, a script), have it checked. ")
          .Append("If a part fails its check or comes back incomplete, run that part again one model up (haiku, then sonnet, then opus) and say so. ")
          .Append("Do not repeat the subagents' searches yourself; check only what you need to trust the result. ")
          .Append("When all stages are done, give me a short summary.");
        return sb.ToString();
    }

    /// <summary>
    /// The model for a part, picked by its role rather than by the planner:
    /// a fixed rule is cheaper and predictable. Small models only where a
    /// mistake is cheap to spot (finding files, reading logs, running
    /// tests); edits and judgement stay on the larger ones.
    /// </summary>
    public static string ModelFor(string role, string agentType, bool writes)
    {
        var r = (role ?? "").ToLowerInvariant();
        if (r.Contains("review") || r.Contains("architect") || r.Contains("plan")) return "opus";
        if (writes) return "sonnet";
        if (r.Contains("explor") || r.Contains("search") || r.Contains("test") || r.Contains("log")
            || string.Equals(agentType, "Explore", StringComparison.OrdinalIgnoreCase))
            return "haiku";
        return "sonnet";
    }

    /// <summary>Why a model choice is risky for a part, or null.</summary>
    public static string? ModelWarning(string model, bool writes) =>
        writes && string.Equals(model, "haiku", StringComparison.OrdinalIgnoreCase)
            ? "Haiku is best for searching, logs and tests. Changes to code usually need Sonnet or Opus."
            : null;

    /// <summary>Counted per turn of the main conversation, from its steps.</summary>
    public sealed class TurnSignals
    {
        public int SearchSteps { get; set; }
        public int EditSteps { get; set; }
        public int AgentSteps { get; set; }
        public int TotalSteps { get; set; }
    }

    public static bool IsSearchTool(string toolName) =>
        toolName is "Read" or "Grep" or "Glob" or "LS" or "NotebookRead";

    public static bool IsEditTool(string toolName) =>
        toolName is "Edit" or "Write" or "MultiEdit" or "NotebookEdit";

    /// <summary>
    /// Why this task looks worth splitting, or null when it does not. Only
    /// measured signs count: the user should see the reason and be able to
    /// check it.
    /// </summary>
    /// <param name="task">The user's message that started the turn.</param>
    /// <param name="signals">What the main conversation did in the turn.</param>
    /// <param name="modelAlias">The main conversation's model, as shown in the picker.</param>
    public static string? SuggestionReason(string task, TurnSignals signals, string? modelAlias)
    {
        // Already split, or too little happened to tell.
        if (signals.AgentSteps > 0 || signals.TotalSteps < 4) return null;

        var expensive = IsExpensiveModel(modelAlias);
        var parts = CountParts(task);
        var reasons = new List<string>();

        if (expensive && signals.SearchSteps >= 8)
            reasons.Add($"{ModelName(modelAlias)} made {signals.SearchSteps} search and read calls that a cheaper model can do");
        if (signals.EditSteps >= 6)
            reasons.Add($"{signals.EditSteps} edits that could run in parallel");
        if (parts >= 3)
            reasons.Add($"your message lists {parts} separate parts");

        if (reasons.Count == 0) return null;
        // One strong sign is not enough on a cheap model: splitting has a cost of its own.
        if (!expensive && reasons.Count < 2) return null;
        return Capitalize(string.Join("; ", reasons)) + ".";
    }

    private static readonly Regex InlineNumberRe = new Regex(@"(?<!\S)(\d{1,2})[.)]\s+\S", RegexOptions.CultureInvariant);

    private static readonly Regex ListItemRe = new Regex(@"^\s*(?:\d+[.)]|[-*•])\s+\S", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The number of parts a message names: list items, or "then"/"and also" joins.</summary>
    public static int CountParts(string task)
    {
        if (string.IsNullOrWhiteSpace(task)) return 0;
        var items = ListItemRe.Matches(task).Count;
        if (items >= 2) return items;

        // A numbered list typed on one line: "1. Read … 2. Read … 3. …".
        // Counted only while the numbers run 1, 2, 3, so a version number or
        // a line number in the text does not count as a part.
        var next = 1;
        foreach (Match m in InlineNumberRe.Matches(task))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n == next) next++;
        }
        if (next - 1 >= 2) return next - 1;

        var joins = Regex.Matches(task, @"\b(then|and then|after that|also|finally)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        return joins >= 2 ? joins + 1 : 0;
    }

    public static bool IsExpensiveModel(string? modelAlias)
    {
        var m = (modelAlias ?? "").ToLowerInvariant();
        return m.Contains("opus") || m.Contains("fable");
    }

    private static string ModelName(string? modelAlias)
    {
        var m = (modelAlias ?? "").ToLowerInvariant();
        return m.Contains("fable") ? "Fable" : m.Contains("opus") ? "Opus" : "The main model";
    }

    private static string OneLine(string text, int max)
    {
        var line = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return line.Length <= max ? line : line.Substring(0, max) + "…";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
