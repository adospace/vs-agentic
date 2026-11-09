using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

public sealed partial class ClaudeCliChatService
{
    // The last system/init of the running process: what the setup check
    // reports for this chat. Cleared on every process start.
    private volatile CliInitEvent? _lastInit;

    /// <summary>
    /// The CLI behind this chat, as its last <c>system/init</c> described it:
    /// version, model, permission mode and MCP servers. Before the first
    /// message there is no process, and so nothing to report but that.
    /// </summary>
    public IReadOnlyList<SetupCheckItem> DescribeSession()
    {
        var items = new List<SetupCheckItem>();
        var init = _lastInit;

        if (init is null)
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Info, "Claude Code session",
                _host.IsRunning
                    ? "Started, waiting for its first turn. Run `/doctor` again after Claude answers to see its MCP servers."
                    : "Not started yet; it starts with the first message. Run `/doctor` again after Claude answers to see its MCP servers."));
            return items;
        }

        var facts = new List<string>();
        if (!string.IsNullOrEmpty(init.Version)) facts.Add("CLI " + init.Version);
        if (!string.IsNullOrEmpty(init.Model)) facts.Add("model `" + init.Model + "`");
        var mode = ReportedPermissionMode ?? init.PermissionMode;
        if (!string.IsNullOrEmpty(mode)) facts.Add("permission mode " + mode);
        items.Add(new SetupCheckItem(SetupCheckStatus.Info, "Claude Code session",
            (facts.Count == 0 ? "Running" : string.Join(", ", facts)) + "."));

        if (init.McpServers is null)
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Warning, "MCP servers",
                "This CLI does not report its MCP servers, so it is not known whether the permission server connected."));
            return items;
        }

        var ours = init.McpServers.FirstOrDefault(s => s.Name == ClaudeCliProcessHost.PermissionServerName);
        if (ours is null || ours.IsUnavailable)
        {
            var log = Path.Combine(ClaudeCliProcessHost.HelperLogDirectory,
                "vsagentic-mcp-helper-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            items.Add(new SetupCheckItem(SetupCheckStatus.Error, "Permission server in this chat",
                $"Not connected (status {ours?.Status ?? "missing"}). Tools that need your approval stop the turn. " +
                $"Start a new chat; if it happens again, attach `{log}` to a bug report."));
        }
        else
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Ok, "Permission server in this chat", "Connected."));
        }

        foreach (var server in init.McpServers.Where(s => s.Name != ClaudeCliProcessHost.PermissionServerName))
        {
            var title = "MCP server " + server.Name;
            if (server.NeedsSignIn)
                items.Add(new SetupCheckItem(SetupCheckStatus.Warning, title, "Needs sign-in. Run `claude` in a terminal, then `/mcp`, to sign in."));
            else if (server.IsUnavailable)
                items.Add(new SetupCheckItem(SetupCheckStatus.Warning, title, $"Not connected (status {server.Status}). Claude cannot use its tools."));
            else
                items.Add(new SetupCheckItem(SetupCheckStatus.Ok, title, server.Status == CliMcpServer.Connected ? "Connected." : Capitalize(server.Status) + "."));
        }

        foreach (var error in init.McpServerErrors)
        {
            items.Add(new SetupCheckItem(SetupCheckStatus.Warning, "MCP server " + error.Name,
                "Skipped, its configuration is not valid" + (error.Message is { Length: > 0 } message && !string.IsNullOrWhiteSpace(message) ? ": " + message.Replace("`", "'") + "." : ".")));
        }

        return items;
    }

    public event Action? CliNotFound;

    private void RaiseCliNotFound()
    {
        try { CliNotFound?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "[ClaudeCli] CliNotFound handler threw"); }
    }

    public void RestartAfterSetupFix()
    {
        bool busy;
        lock (_activeTurnLock) { busy = _turns.Count > 0 || _cliTurn is not null; }
        if (busy || !_host.IsRunning) return;

        // As for a new sign-in: the next message starts a fresh process, which
        // reads the new path and credentials. The conversation is resumed.
        try
        {
            _host.Stop();
            lock (_dispatcherLock) { _dispatcherTask = null; }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeCli] Could not stop the CLI after a setup fix");
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
