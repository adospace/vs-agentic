using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VsAgentic.Services.IdeContext;

/// <summary>
/// One source of IDE state for Claude: tools Claude calls through the
/// <c>vsagentic</c> MCP server when it needs to know what Visual Studio shows.
///
/// Implemented by the Visual Studio host. The tools are called off the UI
/// thread; a provider switches to it only where the API it reads demands it.
/// </summary>
public interface IIdeContextProvider
{
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
    /// True when the tool only reads IDE state that Claude could see anyway,
    /// for example by reading files. Such a tool runs without a permission
    /// banner. False sends each call through the banner. Every tool states
    /// this itself; there is no default, so a new tool cannot skip the banner
    /// by accident.
    /// </summary>
    public bool IsReadOnly { get; }

    private readonly Func<JsonElement, CancellationToken, Task<IdeToolResult>> _invoke;

    public IdeTool(
        string name,
        string title,
        string description,
        string inputSchemaJson,
        Func<JsonElement, CancellationToken, Task<IdeToolResult>> invoke,
        bool isReadOnly)
    {
        Name = name;
        Title = title;
        Description = description;

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
