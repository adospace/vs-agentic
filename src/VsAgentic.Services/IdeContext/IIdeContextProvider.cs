using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.IdeContext;

/// <summary>
/// One source of IDE state for Claude. It answers on two levels: a cheap
/// summary the chat can attach to a message, and tools Claude calls through
/// the <c>vsagentic</c> MCP server when it needs the details.
///
/// Implemented by the Visual Studio host. Both levels are called off the UI
/// thread; a provider switches to it only where the API it reads demands it.
/// </summary>
public interface IIdeContextProvider
{
    /// <summary>
    /// The current state in brief, or null when there is nothing worth
    /// saying (no errors, no document open). Must return quickly: a provider
    /// that runs out of time is left out rather than holding up Send.
    /// </summary>
    Task<IdeContextSection?> GetSummaryAsync(CancellationToken cancellationToken);

    /// <summary>The tools this provider adds to the MCP server.</summary>
    IReadOnlyList<IdeTool> Tools { get; }
}

/// <summary>
/// A tool on the <c>vsagentic</c> MCP server. Claude sees it as
/// <c>mcp__vsagentic__{Name}</c>.
/// </summary>
public sealed class IdeTool
{
    public string Name { get; }

    /// <summary>What Claude reads to decide when to call the tool.</summary>
    public string Description { get; }

    /// <summary>The JSON Schema of the arguments, as raw JSON.</summary>
    public string InputSchemaJson { get; }

    /// <summary>The step title in the chat, e.g. "Reading the Error List".</summary>
    public string Title { get; }

    /// <summary>
    /// True when the tool only reads IDE state. Such a tool runs without a
    /// permission banner: it shows Claude nothing it could not already see
    /// by reading files, and a banner for every look at the Error List would
    /// make the tool useless.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// The switch that turns the tool off for one chat, e.g. <see cref="IdeToolGroups.Debugger"/>.
    /// Null for a tool that is always on.
    /// </summary>
    public string? Group { get; }

    private readonly Func<JsonElement, CancellationToken, Task<IdeToolResult>> _invoke;

    public IdeTool(
        string name,
        string title,
        string description,
        string inputSchemaJson,
        Func<JsonElement, CancellationToken, Task<IdeToolResult>> invoke,
        bool isReadOnly = true,
        string? group = null)
    {
        Name = name;
        Title = title;
        Description = description;
        Group = group;

        // Re-emitted on one line: the tool list travels over the pipe as one
        // line, and a schema written across several lines would cut it short.
        // Parsing here also fails an invalid schema at start-up, not mid-chat.
        using (var schema = JsonDocument.Parse(inputSchemaJson))
            InputSchemaJson = JsonSerializer.Serialize(schema.RootElement);

        IsReadOnly = isReadOnly;
        _invoke = invoke;
    }

    /// <param name="arguments">The arguments Claude passed; an empty object when it passed none.</param>
    public Task<IdeToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        => _invoke(arguments, cancellationToken);
}

/// <summary>The groups of tools a chat can turn off.</summary>
public static class IdeToolGroups
{
    /// <summary>
    /// The debugger state: locals and arguments can hold data the user does
    /// not want to send, so the chat has a switch for it.
    /// </summary>
    public const string Debugger = "debugger";
}

/// <summary>What a tool returns to Claude: text, usually JSON.</summary>
public sealed class IdeToolResult
{
    public string Text { get; }
    public bool IsError { get; }

    private IdeToolResult(string text, bool isError)
    {
        Text = text;
        IsError = isError;
    }

    public static IdeToolResult Success(string text) => new(text, false);

    public static IdeToolResult Error(string message) => new(message, true);
}
