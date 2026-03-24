using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Services;

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

    private static readonly JsonElement Schema = JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "task": { "type": "string", "description": "The task for the agent to perform. Be specific about what you want done and what result you expect back." },
            "skill": { "type": "string", "description": "The agent skill to use. 'explore' for codebase exploration and research. 'generic' for general-purpose tasks. Defaults to 'generic'." }
        },
        "required": ["task"]
    }
    """).RootElement.Clone();

    public static ToolDefinition Create(IAgentToolService agentService)
    {
        return new ToolDefinition
        {
            Name = "agent",
            Description = "Delegate a task to a sub-agent that runs its own conversation with a faster AI model (Haiku). The agent uses greb, grop, and bash tools. Choose a skill: 'explore' for codebase exploration, file discovery, and code research; 'generic' for general-purpose tasks. Each invocation starts fresh with no memory of previous calls.",
            InputSchema = Schema,
            InvokeAsync = async (input, ct) =>
            {
                var task = input.GetProperty("task").GetString()!;
                var skill = input.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? "generic"
                    : "generic";

                if (!SkillPrompts.TryGetValue(skill, out var systemPrompt))
                    return $"[Unknown skill '{skill}'. Available skills: {string.Join(", ", SkillPrompts.Keys)}]";

                var result = await agentService.RunAsync(task, systemPrompt, skill, ct);
                return ToolLogger.LogResult("Agent", result);
            }
        };
    }
}
