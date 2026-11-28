namespace VsAgentic.Services.Abstractions;

/// <summary>
/// A quick question about the session that the agent answers on the side:
/// the question waits for no turn, does not stop the running one, and
/// neither it nor the answer becomes part of the conversation.
/// </summary>
public interface ISideQuestions
{
    /// <summary>
    /// Whether the agent can answer side questions. Null until the first
    /// question has been asked; false once the agent has refused the request,
    /// so the host can stop offering it.
    /// </summary>
    bool? SideQuestionsSupported { get; }

    /// <summary>
    /// Asks the question and waits for the answer. Starts the agent's process
    /// when none runs. Never throws for a failure the agent reports; the
    /// answer then carries the error. Cancelling stops the wait only: the
    /// agent is not told, and its answer is dropped.
    /// </summary>
    Task<SideAnswer> AskSideQuestionAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// The answer to a side question: the text, or the reason there is none.
/// </summary>
public sealed class SideAnswer
{
    private SideAnswer(string? text, string? error, bool unsupported)
    {
        Text = text;
        Error = error;
        Unsupported = unsupported;
    }

    /// <summary>The answer, in Markdown. Null when <see cref="Error"/> is set.</summary>
    public string? Text { get; }

    /// <summary>Why there is no answer. Null when there is one.</summary>
    public string? Error { get; }

    /// <summary>The agent does not know side questions at all.</summary>
    public bool Unsupported { get; }

    public static SideAnswer Answered(string text) => new SideAnswer(text, null, false);

    public static SideAnswer Failed(string error) => new SideAnswer(null, error, false);

    public static SideAnswer NotSupported(string error) => new SideAnswer(null, error, true);
}
