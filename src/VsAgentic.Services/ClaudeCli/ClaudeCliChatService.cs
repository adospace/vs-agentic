using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// IChatService implementation that delegates to the Claude Code CLI subprocess.
/// Uses the user's Claude subscription (Pro/Max) instead of a per-token API key.
///
/// CLI invocation:
///   claude -p "{prompt}" --output-format stream-json --verbose --no-session-persistence
///
/// For multi-turn conversations, we use --resume {sessionId} to maintain context.
/// </summary>
public sealed class ClaudeCliChatService : IChatService
{
    private readonly VsAgenticOptions _options;
    private readonly IOutputListener _outputListener;
    private readonly ILogger _logger;

    private string? _cliSessionId;
    private decimal _cumulativeCostUsd;

    public ClaudeCliChatService(
        IOptions<VsAgenticOptions> options,
        IOutputListener outputListener,
        ILogger<ClaudeCliChatService> logger)
    {
        _options = options.Value;
        _outputListener = outputListener;
        _logger = logger;

        // Remove any inherited API key from the host process (e.g. Visual Studio)
        // so the CLI uses subscription auth instead of a stale/invalid API key.
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    }

    public async IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<string>();

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await RunCliAsync(userMessage, channel.Writer, cancellationToken);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var text in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return text;
        }

        await producerTask;
    }

    private async Task RunCliAsync(
        string userMessage,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        var args = BuildArguments();
        _logger.LogDebug("[ClaudeCli] Launching: {Exe} {Args}", _options.ClaudeCliPath, args);

        var psi = new ProcessStartInfo
        {
            FileName = _options.ClaudeCliPath,
            Arguments = args,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            // Pipe the user message via stdin to avoid newlines/special chars
            // breaking command-line argument parsing on Windows
            await process.StandardInput.WriteAsync(userMessage);
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCli] Failed to start CLI process at '{Path}'", _options.ClaudeCliPath);
            var errorItem = new OutputItem
            {
                Id = Guid.NewGuid().ToString("N"),
                ToolName = "ClaudeCli",
                Title = "CLI Error",
                Status = OutputItemStatus.Error,
                Body = $"Failed to start Claude CLI: {ex.Message}\n\nMake sure 'claude' is installed and on your PATH.\nInstall with: npm install -g @anthropic-ai/claude-code"
            };
            _outputListener.OnStepStarted(errorItem);
            _outputListener.OnStepCompleted(errorItem);
            return;
        }

        OutputItem? thinkingItem = null;
        var thinkingBuilder = new StringBuilder();
        DateTime? thinkingStartTime = null;

        OutputItem? responseItem = null;
        var responseBuilder = new StringBuilder();

        OutputItem? toolItem = null;

        try
        {
            // Read stderr in background for diagnostics
            var stderrTask = process.StandardError.ReadToEndAsync();

            var reader = process.StandardOutput;
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                ClaudeCliStreamEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<ClaudeCliStreamEvent>(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogTrace("[ClaudeCli] Skipping non-JSON line: {Line} ({Error})", line, ex.Message);
                    continue;
                }

                if (evt is null) continue;

                switch (evt.Type)
                {
                    case "system":
                        HandleSystemEvent(evt);
                        break;

                    case "assistant":
                        ProcessAssistantMessage(evt, ref thinkingItem, thinkingBuilder,
                            ref thinkingStartTime, ref responseItem, responseBuilder,
                            ref toolItem, writer);
                        break;

                    case "result":
                        HandleResultEvent(evt, ref responseItem, responseBuilder,
                            ref thinkingItem, thinkingStartTime);
                        break;
                }
            }

            await Task.Run(() => process.WaitForExit(), cancellationToken);

            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
                _logger.LogWarning("[ClaudeCli] Process exited with code {Code}. stderr: {Stderr}",
                    process.ExitCode, stderr);

                if (!string.IsNullOrWhiteSpace(stderr) && responseBuilder.Length == 0)
                {
                    var errorItem = new OutputItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ToolName = "ClaudeCli",
                        Title = "CLI Error",
                        Status = OutputItemStatus.Error,
                        Body = stderr.Trim()
                    };
                    _outputListener.OnStepStarted(errorItem);
                    _outputListener.OnStepCompleted(errorItem);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
            }
            throw;
        }
        finally
        {
            // Finalize any open UI blocks
            FinalizeOpenBlocks(ref thinkingItem, thinkingStartTime, ref responseItem, ref toolItem);
            // Turn complete
        }
    }

    private string BuildArguments()
    {
        var sb = new StringBuilder();
        sb.Append("-p --output-format stream-json --verbose");

        // Permission mode — required since the CLI runs non-interactively
        var permFlag = _options.CliPermissionMode switch
        {
            Configuration.CliPermissionMode.BypassPermissions => "bypassPermissions",
            Configuration.CliPermissionMode.Default => "default",
            _ => "acceptEdits",
        };
        sb.Append(" --permission-mode ");
        sb.Append(permFlag);

        // Multi-turn: resume the existing session
        if (_cliSessionId is not null)
        {
            sb.Append(" --resume ");
            sb.Append(EscapeArgument(_cliSessionId));
        }

        return sb.ToString();
    }

    private void HandleSystemEvent(ClaudeCliStreamEvent evt)
    {
        if (evt.Subtype == "init")
        {
            if (evt.SessionId is not null)
            {
                _cliSessionId = evt.SessionId;
                _logger.LogDebug("[ClaudeCli] Session started: {SessionId}, model: {Model}",
                    evt.SessionId, evt.Model);
            }
        }
    }

    private void ProcessAssistantMessage(
        ClaudeCliStreamEvent evt,
        ref OutputItem? thinkingItem, StringBuilder thinkingBuilder,
        ref DateTime? thinkingStartTime,
        ref OutputItem? responseItem, StringBuilder responseBuilder,
        ref OutputItem? toolItem,
        ChannelWriter<string> writer)
    {
        if (evt.Message is not { } msgElement) return;

        // Extract content blocks from the assistant message
        if (!msgElement.TryGetProperty("content", out var contentArray)) return;
        if (contentArray.ValueKind != JsonValueKind.Array) return;

        foreach (var block in contentArray.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeProp)) continue;
            var blockType = typeProp.GetString();

            switch (blockType)
            {
                case "thinking":
                {
                    // Close any open response/tool block
                    FinalizeResponseItem(ref responseItem, responseBuilder);
                    FinalizeToolItem(ref toolItem);

                    var thinking = block.TryGetProperty("thinking", out var tp) ? tp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(thinking)) break;

                    if (thinkingItem is null)
                    {
                        thinkingStartTime = DateTime.UtcNow;
                        thinkingItem = new OutputItem
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            ToolName = "Thinking",
                            Title = "Thinking...",
                            Status = OutputItemStatus.Pending
                        };
                        _outputListener.OnStepStarted(thinkingItem);
                    }

                    thinkingBuilder.Append(thinking);
                    var elapsed = (int)(DateTime.UtcNow - thinkingStartTime!.Value).TotalSeconds;
                    thinkingItem.Delta = thinking;
                    thinkingItem.Body = thinkingBuilder.ToString();
                    thinkingItem.Title = elapsed > 0 ? $"Thought for {elapsed}s" : "Thinking...";
                    _outputListener.OnStepUpdated(thinkingItem);
                    break;
                }

                case "text":
                {
                    // Close thinking block
                    FinalizeThinkingItem(ref thinkingItem, thinkingBuilder, thinkingStartTime);
                    FinalizeToolItem(ref toolItem);

                    var text = block.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(text)) break;

                    if (responseItem is null)
                    {
                        responseItem = new OutputItem
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            ToolName = "AI",
                            Title = "Responding",
                            Status = OutputItemStatus.Pending
                        };
                        _outputListener.OnStepStarted(responseItem);
                    }

                    responseBuilder.Append(text);
                    responseItem.Delta = text;
                    responseItem.Body = responseBuilder.ToString();
                    _outputListener.OnStepUpdated(responseItem);
                    writer.TryWrite(text);
                    break;
                }

                case "tool_use":
                {
                    FinalizeThinkingItem(ref thinkingItem, thinkingBuilder, thinkingStartTime);
                    FinalizeResponseItem(ref responseItem, responseBuilder);
                    FinalizeToolItem(ref toolItem);

                    var toolName = block.TryGetProperty("name", out var np) ? np.GetString() ?? "tool" : "tool";
                    var toolId = block.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";

                    toolItem = new OutputItem
                    {
                        Id = toolId,
                        ToolName = toolName,
                        Title = $"Using {toolName}",
                        Status = OutputItemStatus.Pending
                    };

                    // Try to extract a summary from tool input
                    if (block.TryGetProperty("input", out var input))
                    {
                        toolItem.Body = FormatToolInput(toolName, input);
                    }

                    _outputListener.OnStepStarted(toolItem);
                    break;
                }

                case "tool_result":
                {
                    if (toolItem is not null)
                    {
                        var content = block.TryGetProperty("content", out var cp)
                            ? ExtractToolResultText(cp)
                            : "";

                        toolItem.Status = OutputItemStatus.Success;
                        toolItem.Title = $"Used {toolItem.ToolName}";
                        toolItem.Body = content;
                        toolItem.Delta = null;
                        _outputListener.OnStepCompleted(toolItem);
                        toolItem = null;
                    }
                    break;
                }
            }
        }
    }

    private void HandleResultEvent(
        ClaudeCliStreamEvent evt,
        ref OutputItem? responseItem, StringBuilder responseBuilder,
        ref OutputItem? thinkingItem, DateTime? thinkingStartTime)
    {
        FinalizeThinkingItem(ref thinkingItem, new StringBuilder(), thinkingStartTime);

        if (evt.IsError == true)
        {
            _logger.LogWarning("[ClaudeCli] CLI returned error result: {Result}", evt.Result);

            if (responseItem is not null)
            {
                responseItem.Status = OutputItemStatus.Error;
                responseItem.Title = "Error";
                responseItem.Delta = null;
                _outputListener.OnStepCompleted(responseItem);
                responseItem = null;
            }
            else
            {
                var errorItem = new OutputItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ToolName = "ClaudeCli",
                    Title = "Error",
                    Status = OutputItemStatus.Error,
                    Body = evt.Result ?? "Unknown CLI error"
                };
                _outputListener.OnStepStarted(errorItem);
                _outputListener.OnStepCompleted(errorItem);
            }
        }
        else
        {
            if (evt.CostUsd.HasValue)
                _cumulativeCostUsd += evt.CostUsd.Value;

            _logger.LogDebug("[ClaudeCli] Completed: {Turns} turns, {Duration}ms, ${Cost} (cumulative: ${Cumulative})",
                evt.NumTurns, evt.DurationMs, evt.CostUsd, _cumulativeCostUsd);
        }
    }

    // ── Finalization helpers ───────────────────────────────────────────────

    private void FinalizeThinkingItem(ref OutputItem? item, StringBuilder builder, DateTime? startTime)
    {
        if (item is null) return;
        var elapsed = startTime.HasValue ? (int)(DateTime.UtcNow - startTime.Value).TotalSeconds : 0;
        item.Status = OutputItemStatus.Success;
        item.Title = $"Thought for {elapsed}s";
        item.Delta = null;
        _outputListener.OnStepCompleted(item);
        item = null;
        builder.Clear();
    }

    private void FinalizeResponseItem(ref OutputItem? item, StringBuilder builder)
    {
        if (item is null) return;
        item.Status = OutputItemStatus.Success;
        item.Title = "Response complete";
        item.Delta = null;
        _outputListener.OnStepCompleted(item);
        item = null;
        builder.Clear();
    }

    private void FinalizeToolItem(ref OutputItem? item)
    {
        if (item is null) return;
        if (item.Status == OutputItemStatus.Pending)
        {
            item.Status = OutputItemStatus.Success;
            item.Title = $"Used {item.ToolName}";
            item.Delta = null;
            _outputListener.OnStepCompleted(item);
        }
        item = null;
    }

    private void FinalizeOpenBlocks(
        ref OutputItem? thinkingItem, DateTime? thinkingStartTime,
        ref OutputItem? responseItem,
        ref OutputItem? toolItem)
    {
        FinalizeThinkingItem(ref thinkingItem, new StringBuilder(), thinkingStartTime);
        FinalizeResponseItem(ref responseItem, new StringBuilder());
        FinalizeToolItem(ref toolItem);
    }

    // ── Formatting helpers ────────────────────────────────────────────────

    private static string FormatToolInput(string toolName, JsonElement input)
    {
        try
        {
            // Show the most relevant field for common tools
            if (input.TryGetProperty("command", out var cmd))
                return $"```\n{cmd.GetString()}\n```";
            if (input.TryGetProperty("file_path", out var fp))
                return $"`{fp.GetString()}`";
            if (input.TryGetProperty("pattern", out var pat))
                return pat.GetString() ?? "";

            return input.GetRawText().Length > 200
                ? input.GetRawText()[..200] + "..."
                : input.GetRawText();
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

        // content can be an array of text blocks
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

    // ── Argument escaping ─────────────────────────────────────────────────

    private static string EscapeArgument(string arg)
    {
        // Wrap in double quotes, escaping internal quotes and backslashes
        var escaped = arg
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // ── IChatService members ──────────────────────────────────────────────

    public async Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default)
    {
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
                Arguments = "-p --output-format text --no-session-persistence",
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

            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            var result = await process.StandardOutput.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit(), cancellationToken);

            var title = result.Trim().Trim('"').Trim();
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeCli] Title generation failed, using fallback");
        }

        // Fallback: truncate user message
        var fallback = userMessage.Split('\n')[0].TrimStart('#', ' ', '-');
        return fallback.Length <= 50 ? fallback : fallback[..50] + "…";
    }

    public decimal? GetSessionCost() => _cumulativeCostUsd > 0 ? _cumulativeCostUsd : null;

    public void ClearHistory()
    {
        _cliSessionId = null;
        _cumulativeCostUsd = 0;
        _logger.LogInformation("[ClaudeCli] Session cleared");
    }

    public string SerializeHistory()
    {
        // For CLI mode, the session is managed by the CLI itself.
        // We persist the session ID so we can resume.
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
                // Turn complete
                _logger.LogInformation("[ClaudeCli] Restored session: {SessionId}", _cliSessionId);
            }
        }
        catch (JsonException)
        {
            // Not a CLI session — likely an API session. Start fresh.
            _logger.LogDebug("[ClaudeCli] Could not restore history (not a CLI session)");
        }
    }
}
