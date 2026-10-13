using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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

/// <summary>A model the CLI offers, from its answer to <c>initialize</c>.</summary>
public sealed record CliModelInfo(
    string Value,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> SupportedEffortLevels);

/// <summary>A slash command or skill the CLI offers, from its answer to <c>initialize</c>.</summary>
public sealed record CliCommandInfo(string Name, string? Description, string? ArgumentHint);

/// <summary>
/// What the running CLI said about itself in its answer to <c>initialize</c>:
/// its version, models, commands, output style and permission mode.
///
/// Nothing reads the lists yet. They are parsed here, in one place, so the
/// features that need them (the model picker, a slash command menu) do not
/// each read the raw answer on their own.
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

    internal static CliProcessInfo Read(JsonElement response)
    {
        var info = new CliProcessInfo
        {
            Version = ReadString(response, "claude_code_version"),
            OutputStyle = ReadString(response, "output_style"),
            PermissionMode = ReadString(response, "current_permission_mode"),
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
                    ReadStrings(x.m, "supportedEffortLevels")))
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
/// The <c>system/init</c> event. The CLI sends it at the start of every turn,
/// not once per process, with the session id, the model actually serving the
/// turn, and its capabilities.
/// </summary>
internal sealed class CliInitEvent
{
    public string? SessionId { get; private set; }
    public string? Model { get; private set; }
    public string? Version { get; private set; }
    public string? PermissionMode { get; private set; }
    public CliCapabilities Capabilities { get; private set; } = CliCapabilities.None;
    public IReadOnlyList<string> Tools { get; private set; } = Array.Empty<string>();

    public static CliInitEvent Read(JsonElement evt)
    {
        var init = new CliInitEvent
        {
            SessionId = CliProcessInfo.ReadString(evt, "session_id"),
            Model = CliProcessInfo.ReadString(evt, "model"),
            Version = CliProcessInfo.ReadString(evt, "claude_code_version"),
            PermissionMode = CliProcessInfo.ReadString(evt, "permissionMode"),
            Capabilities = CliCapabilities.Read(evt),
        };

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
