using Serilog;

namespace VsAgentic.Services.Tools;

/// <summary>
/// Static logging helper for tool FormatResult methods.
/// Logs the exact string returned to the AI from each tool call.
/// Uses Serilog directly since tool classes are static and don't have ILogger.
/// </summary>
internal static class ToolLogger
{
    public static string LogResult(string toolName, string result)
    {
        Log.Debug("[{ToolName}] → Returning to AI ({Length} chars):\n{Result}", toolName, result.Length, result);
        return result;
    }
}
