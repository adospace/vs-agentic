using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The protocol features a running CLI says it supports, from the
/// <c>capabilities</c> list of <c>system/init</c>. A feature is detected by
/// name rather than guessed from the version number.
///
/// Only <c>system/init</c> is read. The answer to <c>initialize</c> has a
/// field of the same name, but the SDK does not document it, and CLI 2.1.296
/// lists only <c>ui_surface_v1</c> there.
///
/// The set is open: the CLI adds names over time, and unknown ones are kept
/// but not acted on.
/// </summary>
public sealed class CliCapabilities
{
    /// <summary>The <c>interrupt</c> answer lists the messages still queued.</summary>
    public const string InterruptReceipt = "interrupt_receipt_v1";

    /// <summary><c>interrupt</c> honors <c>cancel_queued</c>.</summary>
    public const string InterruptCancelQueued = "interrupt_cancel_queued_v1";

    /// <summary>
    /// A user message that carries a <c>uuid</c> gets <c>command_lifecycle</c>
    /// events (queued, started, completed, cancelled) under that uuid.
    /// </summary>
    public const string MessageLifecycle = "msg_lifecycle_v1";

    public static readonly CliCapabilities None = new CliCapabilities(Enumerable.Empty<string>());

    private readonly HashSet<string> _names;

    public CliCapabilities(IEnumerable<string> names)
    {
        _names = new HashSet<string>(names, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Names => _names;

    public bool Has(string name) => _names.Contains(name);

    /// <summary>
    /// Reads a <c>capabilities</c> array. A CLI that predates the field does
    /// not send it, which reads as no capabilities.
    /// </summary>
    internal static CliCapabilities Read(JsonElement owner)
    {
        if (!owner.TryGetProperty("capabilities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return None;

        return new CliCapabilities(arr.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .Where(c => c.Length > 0));
    }

    public override string ToString() => _names.Count == 0 ? "(none)" : string.Join(", ", _names.OrderBy(n => n));
}

/// <summary>
/// A model the CLI offers, from its answer to <c>initialize</c>.
/// <see cref="SupportsEffort"/> is null when the CLI does not send the field.
/// </summary>
public sealed record CliModelInfo(
    string Value,
    string? DisplayName,
    string? Description,
    string? ResolvedModel,
    bool? SupportsEffort,
    IReadOnlyList<string> SupportedEffortLevels,
    bool SupportsFastMode = false)
{
    /// <summary>The row the CLI uses for "no model chosen"; the picker's Default.</summary>
    public const string DefaultValue = "default";

    /// <summary>
    /// The list as the model picker shows it. The CLI's <c>default</c> row
    /// becomes the entry with an empty alias, which sends no <c>--model</c>.
    ///
    /// A CLI that sends no effort fields for any model has not said what the
    /// models support, which is not the same as saying they support nothing.
    /// Its entries then leave <see cref="ClaudeModelInfo.SupportedEfforts"/>
    /// null. CLI 2.1.296 sends the fields only for models that have effort:
    /// the Haiku 4.5 row has neither.
    /// </summary>
    public static IReadOnlyList<ClaudeModelInfo> ToCatalog(IReadOnlyList<CliModelInfo> models)
    {
        var effortKnown = models.Any(m => m.SupportsEffort is not null || m.SupportedEffortLevels.Count > 0);

        return models
            .Select(m => new ClaudeModelInfo(
                m.Value == DefaultValue ? "" : m.Value,
                m.DisplayName ?? m.Value,
                m.Description,
                m.ResolvedModel,
                effortKnown ? EffortsOf(m) : null,
                m.SupportsFastMode))
            .ToList();
    }

    private static IReadOnlyList<ClaudeEffort> EffortsOf(CliModelInfo m)
    {
        if (m.SupportsEffort != true && m.SupportedEffortLevels.Count == 0)
            return Array.Empty<ClaudeEffort>();

        var levels = m.SupportedEffortLevels
            .Select(ClaudeEffortExtensions.FromCliValue)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToList();

        // Effort supported, levels not listed: offer them all, as before.
        return levels.Count > 0
            ? levels
            : ((ClaudeEffort[])Enum.GetValues(typeof(ClaudeEffort))).Where(e => e != ClaudeEffort.Default).ToList();
    }
}

/// <summary>A slash command or skill the CLI offers, from its answer to <c>initialize</c>.</summary>
public sealed record CliCommandInfo(string Name, string? Description, string? ArgumentHint);

/// <summary>
/// What the running CLI said about itself in its answer to <c>initialize</c>:
/// its version, models, commands, output style and permission mode.
///
/// The lists are parsed here, in one place, so the features that need them
/// (the model picker, a slash command menu) do not each read the raw answer
/// on their own.
/// </summary>
public sealed class CliProcessInfo
{
    public string? Version { get; private set; }
    public IReadOnlyList<CliModelInfo> Models { get; private set; } = Array.Empty<CliModelInfo>();
    public IReadOnlyList<CliCommandInfo> Commands { get; private set; } = Array.Empty<CliCommandInfo>();
    public string? OutputStyle { get; private set; }
    public string? PermissionMode { get; private set; }

    /// <summary>The plan the CLI is signed in with ("Claude Max"); null for an API key.</summary>
    public string? SubscriptionType { get; private set; }

    /// <summary>Null for a CLI that does not report fast mode.</summary>
    public FastModeStatus? FastMode { get; private set; }

    internal static CliProcessInfo Read(JsonElement response)
    {
        var info = new CliProcessInfo
        {
            Version = ReadString(response, "claude_code_version"),
            OutputStyle = ReadString(response, "output_style"),
            PermissionMode = ReadString(response, "current_permission_mode"),
            FastMode = FastModeStatus.Read(response),
        };

        if (response.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object)
            info.SubscriptionType = ReadString(account, "subscriptionType");

        if (response.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            info.Models = models.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Object)
                .Select(m => (value: ReadString(m, "value"), m))
                .Where(x => !string.IsNullOrEmpty(x.value))
                .Select(x => new CliModelInfo(
                    x.value!,
                    ReadString(x.m, "displayName"),
                    ReadString(x.m, "description"),
                    ReadString(x.m, "resolvedModel"),
                    x.m.TryGetProperty("supportsEffort", out var se)
                        && se.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? se.GetBoolean()
                            : null,
                    ReadStrings(x.m, "supportedEffortLevels"),
                    x.m.TryGetProperty("supportsFastMode", out var sf) && sf.ValueKind == JsonValueKind.True))
                .ToList();
        }

        if (response.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            info.Commands = commands.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object)
                .Select(c => (name: ReadString(c, "name"), c))
                .Where(x => !string.IsNullOrEmpty(x.name))
                .Select(x => new CliCommandInfo(
                    x.name!,
                    ReadString(x.c, "description"),
                    ReadString(x.c, "argumentHint")))
                .ToList();
        }

        return info;
    }

    internal static string? ReadString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : Array.Empty<string>();
}

/// <summary>
/// An MCP server as <c>system/init</c> lists it. <see cref="Status"/> is
/// <c>connected</c>, <c>failed</c>, <c>needs-auth</c>, <c>pending</c> or
/// <c>disabled</c>. <see cref="Source"/> says where its definition came from
/// (<c>dynamic</c> for <c>--mcp-config</c>, <c>project</c>, <c>user</c>,
/// <c>claudeai</c> …) and is absent on older CLIs.
/// </summary>
public sealed record CliMcpServer(string Name, string Status, string? Source)
{
    public const string Connected = "connected";
    public const string Pending = "pending";
    public const string Disabled = "disabled";
    public const string NeedsAuth = "needs-auth";

    /// <summary>
    /// The model cannot use the server, and not because the user turned it
    /// off or because it is still starting.
    /// </summary>
    public bool IsUnavailable => Status != Connected && Status != Pending && Status != Disabled;

    /// <summary>The server uses OAuth and the user has not signed in to it.</summary>
    public bool NeedsSignIn => Status == NeedsAuth;
}

/// <summary>
/// An <c>--mcp-config</c> entry the CLI skipped because its config is not
/// valid, from <c>system/init.mcp_server_errors</c>.
/// </summary>
public sealed record CliMcpServerError(string Name, string? Type, string? Message);

/// <summary>
/// The <c>system/init</c> event. The CLI sends it at the start of every turn,
/// not once per process, with the session id, the model actually serving the
/// turn, its capabilities and the state of its MCP servers.
/// </summary>
internal sealed class CliInitEvent
{
    public string? SessionId { get; private set; }
    public string? Model { get; private set; }
    public string? Version { get; private set; }
    public string? PermissionMode { get; private set; }
    public CliCapabilities Capabilities { get; private set; } = CliCapabilities.None;
    public IReadOnlyList<string> Tools { get; private set; } = Array.Empty<string>();
    public FastModeStatus? FastMode { get; private set; }

    /// <summary>Null when the event has no <c>mcp_servers</c> list at all.</summary>
    public IReadOnlyList<CliMcpServer>? McpServers { get; private set; }

    public IReadOnlyList<CliMcpServerError> McpServerErrors { get; private set; } = Array.Empty<CliMcpServerError>();

    public static CliInitEvent Read(JsonElement evt)
    {
        var init = new CliInitEvent
        {
            SessionId = CliProcessInfo.ReadString(evt, "session_id"),
            Model = CliProcessInfo.ReadString(evt, "model"),
            Version = CliProcessInfo.ReadString(evt, "claude_code_version"),
            PermissionMode = CliProcessInfo.ReadString(evt, "permissionMode"),
            Capabilities = CliCapabilities.Read(evt),
            FastMode = FastModeStatus.Read(evt),
        };

        if (evt.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            init.McpServers = servers.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => (name: CliProcessInfo.ReadString(s, "name"), s))
                .Where(x => !string.IsNullOrEmpty(x.name))
                .Select(x => new CliMcpServer(
                    x.name!,
                    CliProcessInfo.ReadString(x.s, "status") ?? "",
                    CliProcessInfo.ReadString(x.s, "source")))
                .ToList();
        }

        // Present only when an entry was skipped.
        if (evt.TryGetProperty("mcp_server_errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            init.McpServerErrors = errors.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new CliMcpServerError(
                    CliProcessInfo.ReadString(e, "name") ?? "",
                    CliProcessInfo.ReadString(e, "type"),
                    CliProcessInfo.ReadString(e, "message")))
                .ToList();
        }

        if (evt.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var t in tools.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) names.Add(t.GetString() ?? "");
                else if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var n))
                    names.Add(n.GetString() ?? "");
            }
            init.Tools = names;
        }

        return init;
    }
}
