using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;

namespace VsAgentic.UI.ViewModels;

// ── /btw: a side question ─────────────────────────────────────────────────
// Claude answers from what the session holds, including a turn that is still
// running, and the turn goes on. Neither the question nor the answer joins the
// conversation, so the answer is shown in a panel over the chat rather than in
// it, and is not stored with the session.
//
// Typed as a message, "/btw …" would not work: the CLI answers it with "isn't
// available in this environment", waits for the running turn to end first,
// and writes it into the conversation. So the input never sends it.

public partial class ChatSessionViewModel
{
    public const string SideQuestionCommand = "btw";

    private static readonly Regex SideQuestionRe = new Regex(
        @"^/btw(?:\s+(?<q>[\s\S]*\S))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private CancellationTokenSource? _sideQuestionCts;
    private int _sideQuestionCounter;

    /// <summary>
    /// Raised on the UI thread when a side question goes out. Arguments: its
    /// id and the question. A newer question takes the place of an older one.
    /// </summary>
    public event Action<string, string>? SideQuestionAsked;

    /// <summary>
    /// Raised on the UI thread when a side question has its answer. Arguments:
    /// its id, the answer in Markdown, and the error text when there is no
    /// answer (then the answer is null).
    /// </summary>
    public event Action<string, string?, string?>? SideQuestionAnswered;

    /// <summary>
    /// Whether side questions are offered: the button, the shortcut and the
    /// menu row. False once the CLI has refused the request.
    /// </summary>
    public bool CanAskSideQuestions =>
        _chatService is ISideQuestions { SideQuestionsSupported: not false };

    /// <summary>
    /// Asks what is in the input as a side question: the button and
    /// Ctrl+Shift+Enter. Text that already starts with "/btw" goes as it is.
    /// False when the input holds no question; the view then writes "/btw "
    /// into it so the user can type one.
    /// </summary>
    public bool AskInputAsSideQuestion()
    {
        if (!CanAskSideQuestions) return false;
        var typed = InputText.Trim();
        if (typed.Length == 0) return false;

        var match = SideQuestionRe.Match(typed);
        if (match.Success && !match.Groups["q"].Success) return false;
        return TryAskSideQuestion(match.Success ? typed : "/" + SideQuestionCommand + " " + typed);
    }

    /// <summary>
    /// "/btw …" typed into the input and sent while Send is off (a turn runs
    /// on a CLI without message lifecycles, or Stop is pending): a side
    /// question needs no free turn. False for any other text.
    /// </summary>
    public bool TryAskTypedSideQuestion() =>
        CanAskSideQuestions && TryAskSideQuestion(InputText.Trim());

    /// <summary>The row for /btw in the menu under "/", or null when it is not offered.</summary>
    private SlashCommandOption? SideQuestionMenuRow() =>
        CanAskSideQuestions
            ? new SlashCommandOption(new ClaudeCommandInfo(
                SideQuestionCommand,
                "Ask a quick question on the side (Ctrl+Shift+Enter). Claude answers without stopping its work, and the question does not join the conversation.",
                "<question>",
                IsBuiltin: true))
            : null;

    /// <summary>
    /// Handles a message that starts with "/btw". False when it does not; the
    /// message then goes on as usual. "/btw" with no question does nothing,
    /// and leaves the input as it is.
    /// </summary>
    private bool TryAskSideQuestion(string message)
    {
        var match = SideQuestionRe.Match(message);
        if (!match.Success) return false;

        var question = match.Groups["q"].Success ? match.Groups["q"].Value : "";
        if (question.Length == 0) return true;

        InputText = "";
        _ = AskSideQuestionAsync(question);
        return true;
    }

    private async Task AskSideQuestionAsync(string question)
    {
        // The panel shows one question; the wait for an older one ends here.
        _sideQuestionCts?.Cancel();
        _sideQuestionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _sideQuestionCts = cts;

        var id = $"side-{++_sideQuestionCounter}";
        SideQuestionAsked?.Invoke(id, question);

        if (_chatService is not ISideQuestions side)
        {
            SideQuestionAnswered?.Invoke(id, null, "Side questions are not available in this session.");
            return;
        }

        SideAnswer answer;
        try
        {
            answer = await side.AskSideQuestionAsync(question, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            answer = SideAnswer.Failed(ex.Message);
        }

        if (answer.Unsupported)
        {
            // The menu, the button and the shortcut stop offering it.
            RebuildSlashCommands();
            OnPropertyChanged(nameof(CanAskSideQuestions));
            SideQuestionAnswered?.Invoke(id, null,
                "This version of Claude Code cannot answer side questions. Update Claude Code to use /btw.");
            return;
        }
        SideQuestionAnswered?.Invoke(id, answer.Text, answer.Error);
    }

    private void DisposeSideQuestions()
    {
        try { _sideQuestionCts?.Cancel(); } catch { }
        try { _sideQuestionCts?.Dispose(); } catch { }
        _sideQuestionCts = null;
    }
}
