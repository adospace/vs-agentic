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

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            using var empty = JsonDocument.Parse("{}");
            arguments = empty.RootElement.Clone();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeout);
        try
        {
            return await tool.InvokeAsync(arguments, timeout.Token).ConfigureAwait(false);
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

    /// <summary>
    /// The summaries of all providers that answer within <paramref name="budget"/>.
    /// A provider that is late or fails is left out, so the caller never waits
    /// longer than the budget.
    /// </summary>
    public async Task<IReadOnlyList<IdeContextSection>> GetSummariesAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        var tasks = _providers.Select(p => SafeSummaryAsync(p, cts.Token)).ToList();
        var all = Task.WhenAll(tasks);
        await Task.WhenAny(all, Task.Delay(budget, cancellationToken)).ConfigureAwait(false);

        return tasks
            .Where(t => t.Status == TaskStatus.RanToCompletion && t.Result is not null)
            .Select(t => t.Result!)
            .ToList();
    }

    private async Task<IdeContextSection?> SafeSummaryAsync(IIdeContextProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[IdeContext] {Provider} could not summarize", provider.GetType().Name);
            return null;
        }
    }
}
