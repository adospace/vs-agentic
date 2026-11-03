using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.IdeContext;

/// <summary>
/// The IDE context providers of one chat, and the tools they add to the
/// <c>vsagentic</c> MCP server. The permission pipe asks it for the tool list
/// and hands it the calls; the chat service asks it for step titles.
/// </summary>
public sealed class IdeContextRegistry
{
    /// <summary>The prefix the CLI puts before the name of a tool on our server.</summary>
    public const string McpToolPrefix = "mcp__vsagentic__";

    // A tool that takes longer is reported to Claude as failed rather than
    // left to hold the turn open.
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IIdeContextProvider> _providers;
    private readonly Dictionary<string, IdeTool> _tools = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    // The debugger is off until the chat sets the level from the user's
    // setting, so nothing is read before the setting is known.
    private readonly HashSet<string> _disabledGroups = new(StringComparer.Ordinal)
    {
        IdeToolGroups.Debugger,
        IdeToolGroups.DebuggerValues,
    };

    public IdeContextRegistry(IEnumerable<IIdeContextProvider> providers, ILogger<IdeContextRegistry> logger)
    {
        _providers = providers.ToList();
        _logger = logger;

        foreach (var tool in _providers.SelectMany(p => p.Tools))
        {
            if (_tools.ContainsKey(tool.Name))
                _logger.LogWarning("[IdeContext] Tool '{Name}' is registered twice; keeping the first", tool.Name);
            else
                _tools.Add(tool.Name, tool);
        }
    }

    public IReadOnlyCollection<IdeTool> Tools => _tools.Values;

    /// <summary>
    /// Turns a group of tools off or on for this chat. A turned-off tool stays
    /// in the list the CLI got at start-up, but answers that the user turned it
    /// off, and its provider is left out of the summaries.
    /// </summary>
    public void SetGroupEnabled(string group, bool enabled)
    {
        lock (_disabledGroups)
        {
            if (enabled) _disabledGroups.Remove(group);
            else _disabledGroups.Add(group);
        }
        _logger.LogInformation("[IdeContext] Tool group '{Group}' {State}", group, enabled ? "on" : "off");
    }

    private bool IsDisabled(IdeTool tool)
    {
        if (tool.Group is not { } group) return false;
        lock (_disabledGroups)
            return _disabledGroups.Contains(group);
    }

    /// <summary>
    /// True when the tool behind the name is turned off for this chat. Such a
    /// call needs no permission banner: it only answers that it is off.
    /// </summary>
    public bool IsDisabled(string name) => Find(name) is { } tool && IsDisabled(tool);

    /// <summary>What Claude reads when it calls a tool the user turned off.</summary>
    public const string TurnedOffNote =
        "The user has not allowed this. What Claude may read from the debugger is set in " +
        "Tools > Options > VsAgentic, and for one chat with the Debugger switch below the " +
        "message box. Do not retry; if you need it, ask the user to allow it there.";

    /// <summary>
    /// Sets the debugger tool groups to what a chat's access level allows.
    /// </summary>
    public void SetDebuggerAccess(DebuggerAccessLevel level)
    {
        SetGroupEnabled(IdeToolGroups.Debugger, level != DebuggerAccessLevel.Off);
        SetGroupEnabled(IdeToolGroups.DebuggerValues, level == DebuggerAccessLevel.StackAndValues);
    }

    /// <summary>The tool behind a name as the CLI reports it (with or without the MCP prefix).</summary>
    public IdeTool? Find(string name)
    {
        if (name.StartsWith(McpToolPrefix, StringComparison.Ordinal))
            name = name.Substring(McpToolPrefix.Length);
        return _tools.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>The <c>tools</c> array of an MCP <c>tools/list</c> result.</summary>
    public string BuildToolsJson()
    {
        var sb = new StringBuilder("[");
        foreach (var tool in _tools.Values)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append("{\"name\":").Append(JsonSerializer.Serialize(tool.Name));
            sb.Append(",\"description\":").Append(JsonSerializer.Serialize(tool.Description));
            sb.Append(",\"inputSchema\":").Append(tool.InputSchemaJson);
            sb.Append(",\"annotations\":{\"title\":").Append(JsonSerializer.Serialize(tool.Title));
            sb.Append(",\"readOnlyHint\":").Append(tool.IsReadOnly ? "true" : "false");
            sb.Append("}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Runs a tool. Never throws: an unknown tool, a failure or a timeout
    /// comes back as an error result Claude can read.
    /// </summary>
    public async Task<IdeToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var tool = Find(name);
        if (tool is null)
            return IdeToolResult.Error($"Unknown tool: {name}");

        // Not an error: the tool works, the user chose not to share. Claude
        // should say so rather than retry.
        if (IsDisabled(tool))
        {
            return IdeToolResult.Success(JsonSerializer.Serialize(new
            {
                disabled = true,
                note = TurnedOffNote,
            }));
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            using var empty = JsonDocument.Parse("{}");
            arguments = empty.RootElement.Clone();
        }

        // Not disposed: a tool that ignores the token may still hold it after
        // the timeout below has answered for it.
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeout);

        Task<IdeToolResult> invoke;
        try
        {
            invoke = tool.InvokeAsync(arguments, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IdeContext] Tool '{Name}' failed", name);
            return IdeToolResult.Error($"The tool failed in Visual Studio: {ex.Message}");
        }

        // The token alone only stops a tool that checks it. One that waits on
        // a synchronous DTE or COM call while Visual Studio is busy (a modal
        // dialog, a build, a debugger stop) never does, so the delay answers
        // for it.
        var done = await Task.WhenAny(invoke, Task.Delay(ToolTimeout, cancellationToken)).ConfigureAwait(false);
        if (done != invoke)
        {
            timeout.Cancel();
            ObserveAbandoned(name, invoke);
            if (cancellationToken.IsCancellationRequested)
                return IdeToolResult.Error("The call was cancelled.");
            _logger.LogWarning("[IdeContext] Tool '{Name}' timed out", name);
            return IdeToolResult.Error($"Visual Studio did not answer within {ToolTimeout.TotalSeconds:0} seconds.");
        }

        try
        {
            return await invoke.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[IdeContext] Tool '{Name}' timed out", name);
            return IdeToolResult.Error($"Visual Studio did not answer within {ToolTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IdeContext] Tool '{Name}' failed", name);
            return IdeToolResult.Error($"The tool failed in Visual Studio: {ex.Message}");
        }
    }

    // A call the timeout gave up on runs to its end in the background. Its
    // failure is logged, not left unobserved.
    private void ObserveAbandoned(string name, Task<IdeToolResult> invoke)
    {
        invoke.ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                _logger.LogWarning(ex.GetBaseException(), "[IdeContext] Tool '{Name}' failed after its timeout", name);
            else if (!t.IsCanceled)
                _logger.LogInformation("[IdeContext] Tool '{Name}' finished after its timeout", name);
        }, TaskScheduler.Default);
    }
}
