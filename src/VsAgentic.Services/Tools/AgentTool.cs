using System.ComponentModel;
using VsAgentic.Services.Abstractions;
using Microsoft.Extensions.AI;

namespace VsAgentic.Services.Tools;

public static class AgentTool
{
    private static readonly Dictionary<string, string> SkillPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explore"] = """
            You are a code exploration agent. Investigate the codebase and return a structured summary of findings.
            IMPORTANT: Use 'grop' to find files, 'greb' to search content, 'read' to read files. NEVER use bash for these.
            Work autonomously: search, read, and follow references until you have a complete answer.
            """,
        ["generic"] = """
            You are a developer assistant. Complete the task and return a concise result.
            IMPORTANT: Use 'grop' to find files, 'greb' to search content, 'read' to read files. NEVER use bash for these.
            Use 'bash' only for git, builds, or scripts.
            """
    };

    public static AIFunction Create(IAgentToolService agentService)
    {
        return AIFunctionFactory.Create(
            async ([Description("The task for the agent to perform. Be specific about what you want done and what result you expect back.")] string task,
                   [Description("The agent skill to use. 'explore' for codebase exploration and research. 'generic' for general-purpose tasks. Defaults to 'generic'.")] string skill,
                   CancellationToken cancellationToken) =>
            {
                var effectiveSkill = string.IsNullOrWhiteSpace(skill) ? "generic" : skill;

                if (!SkillPrompts.TryGetValue(effectiveSkill, out var systemPrompt))
                    return $"[Unknown skill '{effectiveSkill}'. Available skills: {string.Join(", ", SkillPrompts.Keys)}]";

                var result = await agentService.RunAsync(task, systemPrompt, effectiveSkill, cancellationToken);
                return ToolLogger.LogResult("Agent", result);
            },
            new AIFunctionFactoryOptions
            {
                Name = "agent",
                Description = "Delegate a task to a sub-agent that runs its own conversation with a faster AI model (Haiku). The agent uses greb, grop, and bash tools. Choose a skill: 'explore' for codebase exploration, file discovery, and code research; 'generic' for general-purpose tasks. Each invocation starts fresh with no memory of previous calls."
            });
    }
}
