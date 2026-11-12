using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.Agents;

/// <summary>
/// The message that runs a split plan, and the model for each part. Plain
/// text in, plain text out: the main conversation does
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

    private static string OneLine(string text, int max)
    {
        var line = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return line.Length <= max ? line : line.Substring(0, max) + "…";
    }
}
