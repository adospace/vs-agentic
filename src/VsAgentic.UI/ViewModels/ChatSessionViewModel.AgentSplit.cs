using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Agents;
using VsAgentic.UI.ViewModels.Banners;

namespace VsAgentic.UI.ViewModels;

// ── Splitting a task across agents ─────────────────────────────────────────
// The main conversation stays the orchestrator; the parts go to subagents on
// cheaper models. The ⚡ button and /split ask for a plan, the plan card lets
// the user change a model per part, and "Run with agents" sends the plan to
// the main conversation as an ordinary message.
public partial class ChatSessionViewModel
{
    /// <summary>Makes split plans. Set by the host; null hides the feature.</summary>
    public IAgentSplitPlanner? SplitPlanner
    {
        get => _splitPlanner;
        set { _splitPlanner = value; OnPropertyChanged(nameof(CanSplit)); SplitAcrossAgentsCommand.NotifyCanExecuteChanged(); }
    }
    private IAgentSplitPlanner? _splitPlanner;

    public bool CanSplit => _splitPlanner is not null;

    // The last message the user sent: what a plan is made for.
    private string? _lastTask;
    private CancellationTokenSource? _planCts;

    // Set while a plan runs: the usage before it, for the summary after it.
    private (long Tokens, decimal? Cost, int Parts)? _splitRun;

    private static readonly Regex SplitCommandRe = new Regex(
        @"^/split(?:\s+([\s\S]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"/split" plans the last task; "/split &lt;text&gt;" plans that text.</summary>
    private bool TryRunSplitCommand(string message)
    {
        var match = SplitCommandRe.Match(message);
        if (!match.Success) return false;
        var text = match.Groups[1].Success ? match.Groups[1].Value.Trim() : null;
        StartSplitPlan(text);
        return true;
    }

    /// <summary>
    /// The ⚡ button. What is typed in the message box is the task; with the
    /// box empty, the last message sent.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void SplitAcrossAgents()
    {
        var typed = InputText?.Trim();
        if (!string.IsNullOrEmpty(typed) && !typed!.StartsWith("/", StringComparison.Ordinal))
        {
            InputText = "";
            StartSplitPlan(typed, fromInput: true);
        }
        else
        {
            StartSplitPlan(null);
        }
    }

    private void StartSplitPlan(string? task, bool fromInput = false)
    {
        var typedTask = fromInput || !string.IsNullOrWhiteSpace(task);
        task = string.IsNullOrWhiteSpace(task) ? _lastTask : task;

        if (_splitPlanner is null) return;
        if (!IsTask(task))
        {
            AddNotice("_Type the task in the message box, then press ⚡, or send `/split` followed by the task. Splitting needs a task of a few words, and this chat has none yet._", "split");
            if (typedTask && string.IsNullOrWhiteSpace(InputText) && !string.IsNullOrWhiteSpace(task)) InputText = task!;
            return;
        }
        if (ActiveBanner is not null && ActiveBanner is not SplitPlanBannerViewModel)
        {
            AddNotice("_Answer the open request first, then split the task._", "split");
            return;
        }

        var card = new SplitPlanBannerViewModel(task!, SelectedModel?.DisplayName ?? "main model",
            onRun: parts => RunSplitPlan(task!, parts),
            onAlone: () =>
            {
                CloseSplitPlan();
                // A task that was sent already needs no second message.
                if (!typedTask) return;
                InputText = task!;
                if (SendCommand.CanExecute(null)) SendCommand.Execute(null);
            },
            onCancel: () =>
            {
                CloseSplitPlan();
                // The task came out of the message box; it goes back there.
                if (typedTask && string.IsNullOrWhiteSpace(InputText)) InputText = task!;
            });
        ActiveBanner = card;

        // A long chat is planned from the task alone: a copy of the chat reads
        // all of it again, which made one plan cost dollars. The card says so
        // and offers the copy.
        // A reopened chat reports no size until its first reply; with messages
        // in it, its size is unknown rather than small.
        var context = _usage.ContextTokens;
        var hasHistory = Items.Any(i => i.Type == ChatItemType.User);
        var sizeKnown = context > 0 || !hasHistory;
        RunPlanner(card, task!, withChatHistory: sizeKnown && context <= PlanWithHistoryLimit, context);
    }

    // Above this much context a plan is made without the chat history unless
    // the user asks for it.
    private const long PlanWithHistoryLimit = 60_000;

    private void RunPlanner(SplitPlanBannerViewModel card, string task, bool withChatHistory, long contextTokens)
    {
        if (_splitPlanner is not { } planner) return;
        _planCts?.Cancel();
        _planCts = new CancellationTokenSource();
        var token = _planCts.Token;

        // Planning is well-scoped work: Sonnet, whatever the chat runs on.
        const string model = "sonnet";

        _ = Task.Run(async () =>
        {
            try
            {
                var plan = await planner.PlanAsync(task, model, withChatHistory, token).ConfigureAwait(false);
                Dispatch(() =>
                {
                    if (token.IsCancellationRequested) return;
                    card.ShowPlan(plan);
                    if (!withChatHistory)
                    {
                        var size = contextTokens > 0
                            ? $"The chat holds {FormatSplitTokens(contextTokens)} tokens"
                            : "The chat's size is not known until its next reply";
                        card.OfferChatHistory(
                            $"Planned from the task alone, without this chat's history. {size}, and a plan that reads a long chat again can cost a few dollars, even on Sonnet. The first stage finds what the others need.",
                            () =>
                            {
                                card.Restart("Planning with this chat's history…");
                                RunPlanner(card, task, withChatHistory: true, contextTokens);
                            });
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[VM] Split plan failed");
                Dispatch(() => card.ShowError(ex.Message));
            }
        });
    }

    private void CloseSplitPlan()
    {
        _planCts?.Cancel();
        if (ActiveBanner is SplitPlanBannerViewModel)
            ShowNextPrompt();
        UpdateActivityIndicator();
    }

    private void RunSplitPlan(string task, IReadOnlyList<AgentSplitTask> parts)
    {
        CloseSplitPlan();
        if (parts.Count == 0) return;

        _splitRun = (_usage.TotalTokens, _usage.CostUsd, parts.Count);
        _splitAgentTokens = 0;
        InputText = AgentSplitText.RunMessage(task, parts);
        if (SendCommand.CanExecute(null))
            SendCommand.Execute(null);
    }

    // ── The task, and the summary after a plan ran ───────────────────────

    /// <summary>
    /// A message that asks for work, not "hi", "ok" or "thanks": the only
    /// kind worth a plan, and the one /split falls back to.
    /// </summary>
    private static bool IsTask(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && message!.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length >= 4;

    private void BeginSplitTracking(string message)
    {
        // The plan's own message is not a new task, and neither is a greeting.
        if (_splitRun is null && IsTask(message)) _lastTask = message;
    }

    // Tokens of the subagents of a running plan, read from the totals their
    // finished steps carry in the header ("… · 31.3k tokens · 30 s").
    private long _splitAgentTokens;
    private static readonly Regex AgentTokensRe = new Regex(@"· ([\d.]+)(k?) tokens", RegexOptions.CultureInvariant);

    private void NoteFinishedStepForSplit(OutputItem item)
    {
        if (_splitRun is null || item.ToolName != "Agent" || item.ParentId is not null) return;
        var m = AgentTokensRe.Match(item.Title ?? "");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return;
        _splitAgentTokens += (long)(m.Groups[2].Value == "k" ? n * 1000 : n);
    }

    /// <summary>After a turn: the summary of a plan that ran.</summary>
    private void AfterTurnForSplit()
    {
        if (_splitRun is { } run)
        {
            _splitRun = null;
            var tokens = Math.Max(0, _usage.TotalTokens - run.Tokens);
            var cost = run.Cost is decimal before && _usage.CostUsd is decimal after ? after - before : (decimal?)null;
            var agents = _splitAgentTokens;
            _splitAgentTokens = 0;
            var line = $"_Done with {run.Parts} {(run.Parts == 1 ? "agent" : "agents")}.";
            if (agents > 0) line += $" The agents used {FormatSplitTokens(agents)} tokens.";
            line += $" The whole turn used {FormatSplitTokens(tokens)}";
            if (cost is decimal c && c > 0) line += " ($" + c.ToString("0.00", CultureInfo.InvariantCulture) + ")";
            line += ", which includes the main conversation re-reading its context at each step. Each agent's model and tokens are in its step above._";
            AddNotice(line, "split-done");
            return;
        }
    }

    private static string FormatSplitTokens(long tokens) =>
        tokens >= 1000
            ? (tokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : tokens.ToString(CultureInfo.InvariantCulture);
}
