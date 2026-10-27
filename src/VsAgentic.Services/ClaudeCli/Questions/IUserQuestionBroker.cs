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

    /// <summary>
    /// Raised with the tool_use id when a pending question is settled without
    /// the user's answer: Stop, or teardown of the CLI process. The card's own
    /// callback never runs in that case, so the UI needs this to take the card
    /// down instead of leaving a dead question on screen.
    /// </summary>
    event Action<string>? PendingCancelled;

    /// <summary>
    /// Raised with the tool_use id when the CLI takes back a question it no
    /// longer needs answered, for example because its turn was interrupted.
    /// </summary>
    event Action<string>? PendingWithdrawn;

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

    /// <summary>
    /// Settles one question the CLI has taken back. The answer goes nowhere;
    /// this only frees the pipe and raises <see cref="PendingWithdrawn"/>.
    /// </summary>
    void Withdraw(string toolUseId);
}
