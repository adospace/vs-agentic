using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// IChatService implementation backed by a single long-running Claude CLI
/// process driven via the bidirectional stream-json protocol.
///
///  - Multi-turn conversations reuse the same subprocess; session state lives
///    inside the CLI for as long as the process is alive.
///  - Each <see cref="SendMessageAsync"/> call writes one user message line
///    and consumes events until the matching <c>result</c> event arrives.
///  - Tool permission requests are intercepted via an in-process MCP server
///    and surfaced through <see cref="IPermissionBroker"/>.
///  - <c>AskUserQuestion</c> tool calls are surfaced through
///    <see cref="IUserQuestionBroker"/>; the answers are returned to the CLI
///    as a tool_result block on the next stdin write.
/// </summary>
public sealed class ClaudeCliChatService : IChatService, IDisposable
{
    private readonly VsAgenticOptions _options;
    private readonly IOutputListener _outputListener;
    private readonly IUserQuestionBroker _questionBroker;
    private readonly ClaudeCliProcessHost _host;
    private readonly ILogger _logger;

    private string? _cliSessionId;
    private decimal _cumulativeCostUsd;
    private Task? _dispatcherTask;
    private readonly object _dispatcherLock = new object();

    // The dispatcher consumes events from the host and routes them to whichever
    // turn is currently active. We only ever have one active turn at a time —
    // SendMessageAsync calls are serialized by the UI (IsBusy gate) — so a
    // single mutable reference is enough.
    private TurnState? _activeTurn;
    private readonly object _activeTurnLock = new object();

    public ClaudeCliChatService(
        IOptions<VsAgenticOptions> options,
        IOutputListener outputListener,
        IUserQuestionBroker questionBroker,
        ClaudeCliProcessHost host,
        ILogger<ClaudeCliChatService> logger)
    {
        _options = options.Value;
        _outputListener = outputListener;
        _questionBroker = questionBroker;
        _host = host;
        _logger = logger;

        // Strip any inherited API key from the host process so child CLI uses
        // subscription auth.
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    }

    public async IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Lazy start: bring the long-running process up if it's not running.
        try
        {
            _host.SetResumeSessionId(_cliSessionId);
            await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            EnsureDispatcherStarted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] Failed to start CLI process");
            EmitFatalError($"Failed to start Claude CLI: {ex.Message}\n\nMake sure 'claude' is installed and on your PATH.\nInstall with: npm install -g @anthropic-ai/claude-code");
            yield break;
        }

        var turn = new TurnState();
        lock (_activeTurnLock)
        {
            _activeTurn = turn;
        }

        // Send the user message line.
        try
        {
            var line = StreamJsonProtocol.BuildUserTextMessage(userMessage);
            await _host.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] Failed to write user message to stdin");
            EmitFatalError($"Failed to send message to Claude CLI: {ex.Message}");
            ClearActiveTurn(turn);
            yield break;
        }

        // Stream text deltas to the caller as they arrive; complete on result.
        var reader = turn.TextDeltas.Reader;
        while (true)
        {
            ValueTask<bool> wait;
            try
            {
                wait = reader.WaitToReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ClearActiveTurn(turn);
                throw;
            }

            bool more;
            try { more = await wait.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                ClearActiveTurn(turn);
                throw;
            }

            if (!more) break;
            while (reader.TryRead(out var text))
                yield return text;
        }

        ClearActiveTurn(turn);
    }

    private void ClearActiveTurn(TurnState turn)
    {
        lock (_activeTurnLock)
        {
            if (ReferenceEquals(_activeTurn, turn))
                _activeTurn = null;
        }
    }

    private void EnsureDispatcherStarted()
    {
        lock (_dispatcherLock)
        {
            if (_dispatcherTask is { IsCompleted: false }) return;
            _dispatcherTask = Task.Run(DispatcherLoopAsync);
        }
    }

    private async Task DispatcherLoopAsync()
    {
        try
        {
            var reader = _host.EventReader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    try { await DispatchEventAsync(evt).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[ClaudeCli] dispatcher: event handler crashed");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] dispatcher loop crashed");
        }
        finally
        {
            // Process exited unexpectedly; tear down any active turn so callers unblock.
            TurnState? turn;
            lock (_activeTurnLock) { turn = _activeTurn; _activeTurn = null; }
            if (turn != null)
            {
                FinalizeOpenBlocks(turn);
                turn.TextDeltas.Writer.TryComplete();
            }
        }
    }

    private async Task DispatchEventAsync(JsonElement evt)
    {
        if (!evt.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();

        switch (type)
        {
            case "system":
                HandleSystemEvent(evt);
                return;

            case "assistant":
                await HandleAssistantEventAsync(evt).ConfigureAwait(false);
                return;

            case "user":
                // The CLI echoes user messages and tool_results; nothing to do here.
                return;

            case "result":
                HandleResultEvent(evt);
                return;
        }
    }

    private void HandleSystemEvent(JsonElement evt)
    {
        var subtype = evt.TryGetProperty("subtype", out var s) ? s.GetString() : null;
        if (subtype != "init") return;
        if (evt.TryGetProperty("session_id", out var sid))
        {
            _cliSessionId = sid.GetString();
            _logger.LogDebug("[ClaudeCli] Session started: {SessionId}", _cliSessionId);
        }
        // Diagnostic: dump the available tool names so we can verify whether
        // AskUserQuestion is registered in headless mode.
        if (evt.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var t in tools.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) names.Add(t.GetString() ?? "");
                else if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var n))
                    names.Add(n.GetString() ?? "");
            }
            _logger.LogInformation("[ClaudeCli] Available tools ({Count}): {Tools}", names.Count, string.Join(", ", names));
        }
    }

    private async Task HandleAssistantEventAsync(JsonElement evt)
    {
        TurnState? turn;
        lock (_activeTurnLock) { turn = _activeTurn; }
        if (turn == null) return;

        if (!evt.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var contentArr)) return;
        if (contentArr.ValueKind != JsonValueKind.Array) return;

        foreach (var block in contentArr.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            switch (bt.GetString())
            {
                case "thinking":
                    HandleThinkingBlock(turn, block);
                    break;

                case "text":
                    HandleTextBlock(turn, block);
                    break;

                case "tool_use":
                    await HandleToolUseBlockAsync(turn, block).ConfigureAwait(false);
                    break;

                case "tool_result":
                    HandleToolResultBlock(turn, block);
                    break;
            }
        }
    }

    private void HandleThinkingBlock(TurnState turn, JsonElement block)
    {
        FinalizeResponseItem(turn);
        FinalizeToolItem(turn);

        var thinking = block.TryGetProperty("thinking", out var tp) ? tp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(thinking)) return;

        if (turn.ThinkingItem == null)
        {
            turn.ThinkingStartTime = DateTime.UtcNow;
            turn.ThinkingItem = new OutputItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ToolName = "Thinking",
                Title = "Thinking...",
                Status = OutputItemStatus.Pending
            };
            _outputListener.OnStepStarted(turn.ThinkingItem);
        }

        turn.ThinkingBuilder.Append(thinking);
        var elapsed = (int)(DateTime.UtcNow - turn.ThinkingStartTime!.Value).TotalSeconds;
        turn.ThinkingItem.Delta = thinking;
        turn.ThinkingItem.Body = turn.ThinkingBuilder.ToString();
        turn.ThinkingItem.Title = elapsed > 0 ? $"Thought for {elapsed}s" : "Thinking...";
        _outputListener.OnStepUpdated(turn.ThinkingItem);
    }

    private void HandleTextBlock(TurnState turn, JsonElement block)
    {
        FinalizeThinkingItem(turn);
        FinalizeToolItem(turn);

        var text = block.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(text)) return;

        if (turn.ResponseItem == null)
        {
            turn.ResponseItem = new OutputItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ToolName = "AI",
                Title = "Responding",
                Status = OutputItemStatus.Pending
            };
            _outputListener.OnStepStarted(turn.ResponseItem);
        }

        turn.ResponseBuilder.Append(text);
        turn.ResponseItem.Delta = text;
        turn.ResponseItem.Body = turn.ResponseBuilder.ToString();
        _outputListener.OnStepUpdated(turn.ResponseItem);
        turn.TextDeltas.Writer.TryWrite(text);
    }

    private async Task HandleToolUseBlockAsync(TurnState turn, JsonElement block)
    {
        var toolName = block.TryGetProperty("name", out var np) ? np.GetString() ?? "tool" : "tool";
        var toolId = block.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";

        // The MCP permission tool call is internal plumbing — hide from the UI.
        if (toolName == "mcp__vsagentic__approval_prompt")
            return;

        // AskUserQuestion is rendered separately as a question card, not as a tool step.
        if (toolName == "AskUserQuestion")
        {
            _logger.LogInformation("[ClaudeCli] AskUserQuestion tool_use received (id={Id})", toolId);
            await HandleAskUserQuestionAsync(toolId, block).ConfigureAwait(false);
            return;
        }

        FinalizeThinkingItem(turn);
        FinalizeResponseItem(turn);
        FinalizeToolItem(turn);

        var toolTitle = $"Using {toolName}";
        string? toolBody = null;
        if (block.TryGetProperty("input", out var input))
        {
            if (toolName == "Agent" && input.TryGetProperty("description", out var descProp))
                toolTitle = descProp.GetString() ?? toolTitle;
            toolBody = FormatToolInput(toolName, input);
        }

        turn.ToolItem = new OutputItem
        {
            Id = toolId,
            ToolName = toolName,
            Title = toolTitle,
            Status = OutputItemStatus.Pending,
            Body = toolBody
        };
        _outputListener.OnStepStarted(turn.ToolItem);
    }

    private void HandleToolResultBlock(TurnState turn, JsonElement block)
    {
        if (turn.ToolItem == null) return;
        var content = block.TryGetProperty("content", out var cp) ? ExtractToolResultText(cp) : "";
        turn.ToolItem.Status = OutputItemStatus.Success;
        if (turn.ToolItem.ToolName != "Agent")
            turn.ToolItem.Title = $"Used {turn.ToolItem.ToolName}";
        turn.ToolItem.Body = content;
        turn.ToolItem.Delta = null;
        _outputListener.OnStepCompleted(turn.ToolItem);
        turn.ToolItem = null;
    }

    private async Task HandleAskUserQuestionAsync(string toolUseId, JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input))
            return;

        var questions = ParseQuestions(input);
        var request = new UserQuestionRequest(toolUseId, questions, input.Clone());

        IReadOnlyDictionary<string, string> answers;
        try
        {
            answers = await _questionBroker.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] AskUserQuestion broker threw");
            answers = new Dictionary<string, string>();
        }

        // Build the tool_result content as the JSON shape the AskUserQuestion
        // tool expects: { "questions": [...], "answers": { question: label, ... } }
        var resultJson = BuildAskUserQuestionResult(input, answers);
        var line = StreamJsonProtocol.BuildToolResultMessage(toolUseId, resultJson);
        try
        {
            await _host.WriteLineAsync(line, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] Failed to write AskUserQuestion answer");
        }
    }

    private static IReadOnlyList<UserQuestion> ParseQuestions(JsonElement input)
    {
        var list = new List<UserQuestion>();
        if (!input.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var q in qs.EnumerateArray())
        {
            var uq = new UserQuestion
            {
                Question = q.TryGetProperty("question", out var qt) ? qt.GetString() ?? "" : "",
                Header = q.TryGetProperty("header", out var hd) ? hd.GetString() ?? "" : "",
                MultiSelect = q.TryGetProperty("multiSelect", out var ms) && ms.ValueKind == JsonValueKind.True,
            };
            if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    uq.Options.Add(new UserQuestionOption
                    {
                        Label = o.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "",
                        Description = o.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "",
                    });
                }
            }
            list.Add(uq);
        }
        return list;
    }

    private static string BuildAskUserQuestionResult(JsonElement input, IReadOnlyDictionary<string, string> answers)
    {
        // Re-emit the original questions array verbatim plus the answers map.
        var sb = new StringBuilder();
        sb.Append("{\"questions\":");
        if (input.TryGetProperty("questions", out var qs))
            sb.Append(qs.GetRawText());
        else
            sb.Append("[]");
        sb.Append(",\"answers\":");
        sb.Append(JsonSerializer.Serialize(answers));
        sb.Append("}");
        return sb.ToString();
    }

    private void HandleResultEvent(JsonElement evt)
    {
        TurnState? turn;
        lock (_activeTurnLock) { turn = _activeTurn; }
        if (turn == null) return;

        FinalizeOpenBlocks(turn);

        var isError = evt.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        if (isError)
        {
            var resultText = evt.TryGetProperty("result", out var rp) ? rp.GetString() : null;
            _logger.LogWarning("[ClaudeCli] CLI returned error result: {Result}", resultText);

            var errorItem = new OutputItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ToolName = "ClaudeCli",
                Title = "Error",
                Status = OutputItemStatus.Error,
                Body = resultText ?? "Unknown CLI error"
            };
            _outputListener.OnStepStarted(errorItem);
            _outputListener.OnStepCompleted(errorItem);
        }
        else
        {
            if (evt.TryGetProperty("cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                _cumulativeCostUsd += cost.GetDecimal();
        }

        // Signal SendMessageAsync to return.
        turn.TextDeltas.Writer.TryComplete();
    }

    // ── Finalization helpers ───────────────────────────────────────────────

    private void FinalizeThinkingItem(TurnState turn)
    {
        if (turn.ThinkingItem == null) return;
        var elapsed = turn.ThinkingStartTime.HasValue ? (int)(DateTime.UtcNow - turn.ThinkingStartTime.Value).TotalSeconds : 0;
        turn.ThinkingItem.Status = OutputItemStatus.Success;
        turn.ThinkingItem.Title = $"Thought for {elapsed}s";
        turn.ThinkingItem.Delta = null;
        _outputListener.OnStepCompleted(turn.ThinkingItem);
        turn.ThinkingItem = null;
        turn.ThinkingBuilder.Clear();
    }

    private void FinalizeResponseItem(TurnState turn)
    {
        if (turn.ResponseItem == null) return;
        turn.ResponseItem.Status = OutputItemStatus.Success;
        turn.ResponseItem.Title = "Response complete";
        turn.ResponseItem.Delta = null;
        _outputListener.OnStepCompleted(turn.ResponseItem);
        turn.ResponseItem = null;
        turn.ResponseBuilder.Clear();
    }

    private void FinalizeToolItem(TurnState turn)
    {
        if (turn.ToolItem == null) return;
        if (turn.ToolItem.Status == OutputItemStatus.Pending)
        {
            turn.ToolItem.Status = OutputItemStatus.Success;
            if (turn.ToolItem.ToolName != "Agent")
                turn.ToolItem.Title = $"Used {turn.ToolItem.ToolName}";
            turn.ToolItem.Delta = null;
            _outputListener.OnStepCompleted(turn.ToolItem);
        }
        turn.ToolItem = null;
    }

    private void FinalizeOpenBlocks(TurnState turn)
    {
        FinalizeThinkingItem(turn);
        FinalizeResponseItem(turn);
        FinalizeToolItem(turn);
    }

    private void EmitFatalError(string body)
    {
        var errorItem = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "ClaudeCli",
            Title = "CLI Error",
            Status = OutputItemStatus.Error,
            Body = body
        };
        _outputListener.OnStepStarted(errorItem);
        _outputListener.OnStepCompleted(errorItem);
    }

    // ── Formatting helpers ────────────────────────────────────────────────

    private static string FormatToolInput(string toolName, JsonElement input)
    {
        try
        {
            if (toolName == "Agent" && input.TryGetProperty("prompt", out var prompt))
                return prompt.GetString() ?? "";

            if (input.TryGetProperty("command", out var cmd))
                return $"```\n{cmd.GetString()}\n```";
            if (input.TryGetProperty("file_path", out var fp))
                return $"`{fp.GetString()}`";
            if (input.TryGetProperty("pattern", out var pat))
                return pat.GetString() ?? "";

            var raw = input.GetRawText();
            return raw.Length > 200 ? raw.Substring(0, 200) + "..." : raw;
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractToolResultText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text))
                    sb.AppendLine(text.GetString());
            }
            return sb.ToString().TrimEnd();
        }

        return content.GetRawText();
    }

    // ── IChatService members ──────────────────────────────────────────────

    public async Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        // Title generation stays one-shot — multiplexing onto the long-running
        // process would require a session fork. A short transient subprocess
        // is fine here.
        const string titlePrompt =
            "Generate a short title (max 6 words) for a coding assistant conversation that starts with the message below. " +
            "The title should capture the intent or topic. Do NOT use quotes or punctuation at the end. " +
            "Respond with ONLY the title, nothing else.\n\nUser message: ";

        try
        {
            var prompt = titlePrompt + userMessage;
            var psi = new ProcessStartInfo
            {
                FileName = _options.ClaudeCliPath,
                Arguments = "-p --output-format text",
                WorkingDirectory = _options.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            process.StandardInput.Close();

            var result = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);

            var title = result.Trim().Trim('"').Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeCli] Title generation failed, using fallback");
        }

        var fallback = userMessage.Split('\n')[0].TrimStart('#', ' ', '-');
        return fallback.Length <= 50 ? fallback : fallback.Substring(0, 50) + "…";
    }

    public decimal? GetSessionCost() => _cumulativeCostUsd > 0 ? _cumulativeCostUsd : null;

    public void ClearHistory()
    {
        _cliSessionId = null;
        _cumulativeCostUsd = 0;
        _host.Stop();
        lock (_dispatcherLock) { _dispatcherTask = null; }
        _logger.LogInformation("[ClaudeCli] Session cleared (process killed)");
    }

    public string SerializeHistory()
    {
        return JsonSerializer.Serialize(new { cliSessionId = _cliSessionId });
    }

    public void RestoreHistory(string serializedHistory)
    {
        try
        {
            using var doc = JsonDocument.Parse(serializedHistory);
            if (doc.RootElement.TryGetProperty("cliSessionId", out var sid))
            {
                _cliSessionId = sid.GetString();
                _logger.LogInformation("[ClaudeCli] Restored session: {SessionId}", _cliSessionId);
            }
        }
        catch (JsonException)
        {
            _logger.LogDebug("[ClaudeCli] Could not restore history (not a CLI session)");
        }
    }

    public void Dispose() => _host.Dispose();

    /// <summary>State for one in-flight turn.</summary>
    private sealed class TurnState
    {
        public OutputItem? ThinkingItem;
        public StringBuilder ThinkingBuilder = new StringBuilder();
        public DateTime? ThinkingStartTime;

        public OutputItem? ResponseItem;
        public StringBuilder ResponseBuilder = new StringBuilder();

        public OutputItem? ToolItem;

        public Channel<string> TextDeltas { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    }
}
