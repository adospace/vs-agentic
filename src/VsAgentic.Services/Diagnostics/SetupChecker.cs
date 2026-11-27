using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;

namespace VsAgentic.Services.Diagnostics;

/// <summary>What the host hands the setup check.</summary>
public sealed class SetupCheckRequest
{
    /// <summary>Claude CLI Path from Tools → Options, as the next chat will use it.</summary>
    public string? ClaudeCliPath { get; init; }

    /// <summary>The folder chats start in; its <c>.claude</c> settings are checked.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Versions of the extension, Visual Studio and Windows, on one line.</summary>
    public string? Environment { get; init; }

    /// <summary>Lines only the host can check, such as the WebView2 runtime.</summary>
    public IReadOnlyList<SetupCheckItem> HostItems { get; init; } = Array.Empty<SetupCheckItem>();

    /// <summary>The chat the check runs in; null from Tools → Options.</summary>
    public ISessionSetupInfo? Session { get; init; }

    public ILogger? Logger { get; init; }
}

/// <summary>
/// Runs the setup check behind <c>/doctor</c> and the button in Tools →
/// Options. Only reads: nothing is changed and nothing is sent to a model.
/// </summary>
public static class SetupChecker
{
    public static async Task<SetupCheckReport> RunAsync(SetupCheckRequest request, CancellationToken ct = default)
    {
        // Independent of each other; the CLI's two calls are the slow part.
        var cli = ClaudeSetupCheck.CheckCliAsync(request.ClaudeCliPath, ct);
        var helper = ClaudeSetupCheck.CheckPermissionServerAsync(request.Logger, ct);
        var settings = Task.Run(() => ClaudeSetupCheck.CheckSettingsFiles(request.WorkingDirectory), ct);

        var claude = new List<SetupCheckItem>();
        claude.AddRange(await cli.ConfigureAwait(false));
        claude.AddRange(await settings.ConfigureAwait(false));

        var vsagentic = new List<SetupCheckItem> { await helper.ConfigureAwait(false) };
        vsagentic.AddRange(request.HostItems);
        vsagentic.Add(new SetupCheckItem(SetupCheckStatus.Info, "Logs",
            $"`{ClaudeCliProcessHost.HelperLogDirectory}`. Today's files have `{DateTime.Now:yyyyMMdd}` in the name; " +
            "attach them to a bug report."));

        var sections = new List<SetupCheckSection>
        {
            new SetupCheckSection("Claude Code", claude),
            new SetupCheckSection("VsAgentic", vsagentic),
        };

        if (request.Session is { } session)
        {
            IReadOnlyList<SetupCheckItem> chat;
            try { chat = session.DescribeSession(); }
            catch (Exception ex)
            {
                chat = new[] { new SetupCheckItem(SetupCheckStatus.Warning, "This chat", $"Could not read its state: {ex.Message}") };
            }
            sections.Add(new SetupCheckSection("This chat", chat));
        }

        return new SetupCheckReport(request.Environment, sections);
    }
}
