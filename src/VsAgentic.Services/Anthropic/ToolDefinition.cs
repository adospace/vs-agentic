using System.Text.Json;

namespace VsAgentic.Services.Anthropic;

/// <summary>
/// Defines a tool that the AI can invoke. Replaces AIFunction/AITool from Microsoft.Extensions.AI.
/// The invoke delegate receives the raw JsonElement from the API — whitespace is preserved exactly.
/// </summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required Func<JsonElement, CancellationToken, Task<string>> InvokeAsync { get; init; }
}
