using System.Collections.Generic;
using System.Text.Json;

namespace VsAgentic.Services.ClaudeCli.Questions;

/// <summary>
/// One question in an <c>AskUserQuestion</c> tool invocation.
/// Mirrors the input shape documented at
/// https://docs.claude.com/en/docs/agent-sdk/user-input#question-format
/// </summary>
public sealed class UserQuestion
{
    public string Question { get; set; } = "";
    public string Header { get; set; } = "";
    public bool MultiSelect { get; set; }
    public List<UserQuestionOption> Options { get; set; } = new List<UserQuestionOption>();
}

public sealed class UserQuestionOption
{
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
/// A pending <c>AskUserQuestion</c> request from the CLI. The id is the
/// <c>tool_use_id</c> we'll use when sending the answer back over stdin.
/// </summary>
public sealed class UserQuestionRequest
{
    public string ToolUseId { get; }
    public IReadOnlyList<UserQuestion> Questions { get; }
    public JsonElement RawInput { get; }

    public UserQuestionRequest(string toolUseId, IReadOnlyList<UserQuestion> questions, JsonElement rawInput)
    {
        ToolUseId = toolUseId;
        Questions = questions;
        RawInput = rawInput;
    }
}
