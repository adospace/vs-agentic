using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.ClaudeCli.Questions;

/// <summary>
/// Mediates <c>AskUserQuestion</c> tool calls between the CLI process host
/// (which sees them as <c>tool_use</c> events) and the chat UI (which renders
/// the question card and collects answers). Singleton.
///
/// Answer dictionary maps each question's <c>question</c> text to the user's
/// chosen <c>label</c> (or comma-joined labels for multi-select, or free text).
/// </summary>
public interface IUserQuestionBroker
{
    event Action<UserQuestionRequest>? QuestionRequested;

    Task<IReadOnlyDictionary<string, string>> SubmitAsync(
        UserQuestionRequest request,
        CancellationToken cancellationToken);

    void Resolve(string toolUseId, IReadOnlyDictionary<string, string> answers);

    /// <summary>
    /// Resolves every in-flight question with an empty answer dictionary.
    /// Used by the chat Stop button so the dispatcher loop can unblock when
    /// a question banner is stuck (e.g. banner failed to display).
    /// </summary>
    void CancelAllPending();
}
