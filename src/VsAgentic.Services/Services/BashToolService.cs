using System.Diagnostics;
using System.Net;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.Services;

public class BashToolService(
    IOptions<VsAgenticOptions> options,
    IOutputListener outputListener,
    ILogger<BashToolService> logger) : IBashToolService
{
    private readonly VsAgenticOptions _options = options.Value;

    public async Task<BashResult> ExecuteAsync(string command, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(command));

        if (!File.Exists(_options.GitBashPath))
            throw new InvalidOperationException($"Git Bash not found at: {_options.GitBashPath}");

        if (!Directory.Exists(_options.WorkingDirectory))
            throw new InvalidOperationException($"Working directory does not exist: {_options.WorkingDirectory}");

        var item = new OutputItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ToolName = "Bash",
            Title = title,
            Status = OutputItemStatus.Pending
        };

        outputListener.OnStepStarted(item);

        logger.LogDebug("Executing bash command: {Command}", command);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.BashTimeoutSeconds));

        var escapedCommand = command.Replace("\"", "\\\"");

        var psi = new ProcessStartInfo
        {
            FileName = _options.GitBashPath,
            Arguments = $"-c \"{escapedCommand}\"",
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await Task.Run(() => process.WaitForExit());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* best effort */ }
            logger.LogWarning("Bash command timed out after {Timeout}s: {Command}", _options.BashTimeoutSeconds, command);

            item.Status = OutputItemStatus.Error;
            item.Title = title;
            item.BodyMode = OutputBodyMode.Html;
            item.Body = FormatBody(command, "", $"Command timed out after {_options.BashTimeoutSeconds} seconds.");
            outputListener.OnStepCompleted(item);

            return new BashResult(-1, "", $"Command timed out after {_options.BashTimeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Hard safety limit to prevent OOM from runaway commands.
        // The actual output management (spill to temp file) happens in BashTool.FormatResult.
        const int safetyLimit = 512 * 1024; // 512 KB
        if (stdout.Length > safetyLimit)
            stdout = stdout.Substring(0, safetyLimit) + "\n... [hard truncated — output exceeded 512 KB]";
        if (stderr.Length > safetyLimit)
            stderr = stderr.Substring(0, safetyLimit) + "\n... [hard truncated — stderr exceeded 512 KB]";

        logger.LogDebug("Bash command exited with code {ExitCode}", process.ExitCode);

        var result = new BashResult(process.ExitCode, stdout, stderr);

        item.Status = result.ExitCode == 0 ? OutputItemStatus.Success : OutputItemStatus.Error;
        item.BodyMode = OutputBodyMode.Html;
        item.Body = FormatBody(command, result.StandardOutput, result.StandardError);
        outputListener.OnStepCompleted(item);

        return result;
    }

    private static string FormatBody(string command, string stdout, string stderr)
    {
        var output = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
        var encodedCommand = WebUtility.HtmlEncode(command);
        var encodedOutput = string.IsNullOrEmpty(output)
            ? "<em>empty</em>"
            : $"<pre>{WebUtility.HtmlEncode(output)}</pre>";

        return $"""
            <table>
            <tr><td><strong>IN</strong></td><td><code>{encodedCommand}</code></td></tr>
            <tr><td><strong>OUT</strong></td><td>{encodedOutput}</td></tr>
            </table>
            """;
    }
}
