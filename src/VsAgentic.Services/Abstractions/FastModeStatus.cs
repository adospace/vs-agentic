using System.Text.Json;

namespace VsAgentic.Services.Abstractions;

/// <summary>
/// Fast mode as the CLI reports it: <c>fast_mode_state</c> and
/// <c>fast_mode_disabled_reason</c>, sent in the answer to <c>initialize</c>,
/// in <c>system/init</c> and in each <c>result</c>.
///
/// Measured on CLI 2.1.296: a host session starts "off" with the reason
/// <c>sdk_opt_in_required</c>. That reason only means the host has not asked
/// for fast mode yet, so it does not block the switch. After
/// <c>apply_flag_settings {fastMode:true}</c> the state is "on" on a model that
/// has fast mode, and "off" without a reason on one that has not.
/// </summary>
public sealed class FastModeStatus
{
    public const string On = "on";
    public const string Off = "off";
    public const string Cooldown = "cooldown";

    /// <summary>The reason a host session gets until it asks for fast mode.</summary>
    public const string OptInRequired = "sdk_opt_in_required";

    public static readonly FastModeStatus Unknown = new FastModeStatus(null, null);

    public FastModeStatus(string? state, string? disabledReason)
    {
        State = state;
        DisabledReason = disabledReason;
    }

    /// <summary>"on", "off" or "cooldown"; null before the CLI has said.</summary>
    public string? State { get; }

    public string? DisabledReason { get; }

    public bool IsOn => State == On;

    public bool IsCoolingDown => State == Cooldown;

    /// <summary>
    /// True when something outside VsAgentic keeps fast mode off: the plan,
    /// the account's extra usage, an environment variable. Asking for it then
    /// changes nothing.
    /// </summary>
    public bool IsBlocked => DisabledReason is not null && DisabledReason != OptInRequired;

    /// <summary>Why fast mode is off, for a tooltip. Null when nothing blocks it.</summary>
    public string? BlockedText => !IsBlocked ? null : DisabledReason switch
    {
        "free" => "Fast mode needs a paid plan.",
        "preference" => "Fast mode is turned off in your Claude Code settings.",
        "extra_usage_disabled" => "Fast mode is billed as extra usage, which is turned off for this account.",
        "network_error" => "Claude Code could not check whether fast mode is available (network error).",
        "not_first_party" => "Fast mode is only available through Anthropic's own API.",
        "disabled_by_env" => "Fast mode is turned off by an environment variable.",
        "model_not_allowed" => "Fast mode is not allowed for this model.",
        "pending" => "Claude Code is still checking whether fast mode is available.",
        _ => $"Fast mode is not available ({DisabledReason}).",
    };

    /// <summary>
    /// Reads the two fields from an event or answer. Null when it does not
    /// carry them, so a CLI that predates fast mode leaves the status alone.
    /// </summary>
    public static FastModeStatus? Read(JsonElement owner)
    {
        if (!owner.TryGetProperty("fast_mode_state", out var state) || state.ValueKind != JsonValueKind.String)
            return null;

        var reason = owner.TryGetProperty("fast_mode_disabled_reason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
        return new FastModeStatus(state.GetString(), reason);
    }

    public bool SameAs(FastModeStatus other) => State == other.State && DisabledReason == other.DisabledReason;

    public override string ToString() => DisabledReason is null ? State ?? "(unknown)" : $"{State} ({DisabledReason})";
}
