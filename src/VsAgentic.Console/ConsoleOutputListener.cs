using VsAgentic.Services.Abstractions;

namespace VsAgentic.Console;

public class ConsoleOutputListener : IOutputListener
{
    private static readonly object Lock = new();

    public void OnStepStarted(OutputItem item)
    {
        lock (Lock)
        {
            var (symbol, color) = GetStatusIndicator(item.Status);

            System.Console.ForegroundColor = color;
            System.Console.Write($"  {symbol} ");
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write($"[{item.ToolName}] ");
            System.Console.ResetColor();
            System.Console.WriteLine(item.Title);
        }
    }

    public void OnStepUpdated(OutputItem item)
    {
        if (string.IsNullOrEmpty(item.Delta))
            return;

        lock (Lock)
        {
            System.Console.Write(item.Delta);
        }
    }

    public void OnStepCompleted(OutputItem item)
    {
        lock (Lock)
        {
            var (symbol, color) = GetStatusIndicator(item.Status);

            System.Console.ForegroundColor = color;
            System.Console.Write($"  {symbol} ");
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write($"[{item.ToolName}] ");
            System.Console.ResetColor();
            System.Console.WriteLine(item.Title);

            if (!string.IsNullOrEmpty(item.Body) && item.ToolName is not "AI" and not "Thinking")
            {
                WriteBody(item.Body ?? string.Empty);
            }

            System.Console.WriteLine();
        }
    }

    private static void WriteBody(string body)
    {
        // Indent the body content under the step
        var lines = body.Split('\n');
        foreach (var line in lines)
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.Write("    ");
            System.Console.ResetColor();
            System.Console.WriteLine(line.TrimEnd('\r'));
        }
    }

    private static (string Symbol, ConsoleColor Color) GetStatusIndicator(OutputItemStatus status) => status switch
    {
        OutputItemStatus.Pending => ("○", ConsoleColor.DarkGray),
        OutputItemStatus.Success => ("●", ConsoleColor.Green),
        OutputItemStatus.Error => ("●", ConsoleColor.Red),
        OutputItemStatus.Info => ("○", ConsoleColor.Gray),
        _ => ("○", ConsoleColor.Gray)
    };
}
