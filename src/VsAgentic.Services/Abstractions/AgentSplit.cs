using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// One part of a task handed to a subagent: who does it, on which model,
/// and when. Parts with the same <see cref="Stage"/> run at the same time.
/// </summary>
public sealed class AgentSplitTask
{
    public string Role { get; set; } = "";

    /// <summary>The kind of subagent that runs the part, e.g. Explore or general-purpose.</summary>
    public string AgentType { get; set; } = "";

    /// <summary>A model alias: haiku, sonnet or opus.</summary>
    public string Model { get; set; } = "";

    public string Task { get; set; } = "";
    public int Stage { get; set; } = 1;

    /// <summary>Whether the part changes files.</summary>
    public bool Writes { get; set; }

    /// <summary>The planner's guess of what the part reads and writes, in tokens; 0 when it gave none.</summary>
    public long EstimatedTokens { get; set; }

    /// <summary>The planner's guess of the part's tool calls; 0 when it gave none.</summary>
    public int EstimatedSteps { get; set; }
}

/// <summary>How the main conversation proposes to split a task.</summary>
public sealed class AgentSplitPlan
{
    public bool WorthSplitting { get; set; }
    public string Reason { get; set; } = "";
    public List<AgentSplitTask> Tasks { get; set; } = new List<AgentSplitTask>();

    /// <summary>What making the plan cost, when the agent reports it.</summary>
    public decimal? PlanningCostUsd { get; set; }
}

/// <summary>
/// Makes a plan for splitting a task across subagents, from what the session
/// knows so far, without adding to the session's own conversation.
/// </summary>
public interface IAgentSplitPlanner
{
    /// <param name="task">The task in the user's words.</param>
    /// <param name="model">The model that makes the plan, as an alias or id.</param>
    /// <param name="withChatHistory">
    /// Plan in a copy of the chat, so the plan knows what the chat has read.
    /// That reads the whole chat again, which costs more the longer it is;
    /// without it the plan sees the task alone.
    /// </param>
    Task<AgentSplitPlan> PlanAsync(string task, string? model, bool withChatHistory, CancellationToken cancellationToken);
}

/// <summary>
/// Where the user's choices about splitting live: suggestions on or off for
/// everything and off for single projects ("Never for this project"), the
/// price list the estimate uses, and the earlier runs that correct it.
/// </summary>
public interface IAgentSplitSettings
{
    bool SuggestionsEnabled { get; }
    bool IsOffFor(string projectFolder);
    void TurnOffFor(string projectFolder);
    void TurnOnFor(string projectFolder);

    /// <summary>The price list from Tools › Options › VsAgentic › Prices.</summary>
    Agents.ModelPriceList Prices { get; }

    /// <summary>The last plans that ran, estimate against measured.</summary>
    Agents.SplitCalibration Calibration { get; }

    void RecordOutcome(Agents.SplitOutcome outcome);
}
