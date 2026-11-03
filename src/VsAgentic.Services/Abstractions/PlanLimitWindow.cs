using System;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// One plan rate-limit window as Claude Code reports it for the account: the
/// 5-hour window, the weekly one, or a weekly window scoped to one model.
///
/// Unlike the local token estimate, this is the server's own reading. It covers
/// everything the account spends — the terminal, other IDEs, claude.ai — not
/// only what VsAgentic sent.
/// </summary>
/// <param name="Name">Label for the window, e.g. "Fable" for a model-scoped one.</param>
/// <param name="Percent">Share of the limit used, 0 to 100.</param>
/// <param name="ResetsAt">When the window resets, if the CLI said.</param>
public sealed record PlanLimitWindow(string Name, double Percent, DateTimeOffset? ResetsAt)
{
    public double Fraction => Math.Min(1d, Math.Max(0d, Percent / 100d));
}
