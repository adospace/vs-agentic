using System;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Completes the event channel of a CLI run that ended on its own rather than
/// through <see cref="ClaudeCliProcessHost.Stop"/>. Carries what is left to
/// explain the exit when the CLI did not write a result event first: the exit
/// code and the last lines it wrote to stderr.
/// </summary>
internal sealed class CliProcessExitedException : Exception
{
    public CliProcessExitedException(int? exitCode, string stderrTail)
        : base(exitCode is { } code
            ? $"Claude CLI exited with code {code}"
            : "Claude CLI closed its output")
    {
        ExitCode = exitCode;
        StderrTail = stderrTail;
    }

    public int? ExitCode { get; }

    public string StderrTail { get; }
}
