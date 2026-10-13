using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// The plan rate-limit readings the CLI last reported, shared by every session.
///
/// The limits belong to the account, not to a session, so one reading serves
/// all open chats: a turn in one session updates the meters in the others, and
/// a session that has not run a turn yet shows what the others learned.
///
/// Two sources feed it. The <c>rate_limit_event</c> the CLI sends during a turn
/// when the reading changes, and the answer to a <c>get_usage</c> control
/// request sent after each turn. The second also carries the model-scoped
/// weekly windows. Both are kept in memory only: a reading from before a
/// restart may be hours old, and the local estimate covers the gap until the
/// first turn.
/// </summary>
public sealed class PlanUsageStore
{
    private static readonly Lazy<PlanUsageStore> LazyShared = new(() => new PlanUsageStore());

    /// <summary>
    /// Process-wide instance. Sessions each build their own DI container, so a
    /// container singleton would give one store per session.
    /// </summary>
    public static PlanUsageStore Shared => LazyShared.Value;

    private readonly object _gate = new();
    private PlanLimitWindow? _fiveHour;
    private PlanLimitWindow? _sevenDay;
    private IReadOnlyList<PlanLimitWindow> _models = Array.Empty<PlanLimitWindow>();

    /// <summary>Raised after a reading changes, on the thread that applied it.</summary>
    public event Action? Changed;

    /// <summary>
    /// The current readings. A window whose reset time has passed is dropped:
    /// its percentage describes a window that no longer exists, and the next
    /// turn brings the new one.
    /// </summary>
    public PlanLimits Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            var models = new List<PlanLimitWindow>();
            foreach (var m in _models)
                if (Current(m, now) is { } current) models.Add(current);

            return new PlanLimits(Current(_fiveHour, now), Current(_sevenDay, now), models);
        }
    }

    private static PlanLimitWindow? Current(PlanLimitWindow? window, DateTimeOffset now) =>
        window is { ResetsAt: { } resetsAt } && resetsAt <= now ? null : window;

    /// <summary>
    /// Applies the <c>response</c> body of a successful <c>get_usage</c> answer.
    /// <c>rate_limits_available</c> false means plan limits do not apply to this
    /// sign-in (API key, Bedrock, Vertex); every reading is then dropped and the
    /// meters go back to the local estimate.
    /// </summary>
    public void ApplyUsageResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return;

        if (response.TryGetProperty("rate_limits_available", out var available)
            && available.ValueKind == JsonValueKind.False)
        {
            Update(null, null, Array.Empty<PlanLimitWindow>(), keepMissingWindows: false);
            return;
        }

        if (!response.TryGetProperty("rate_limits", out var limits)
            || limits.ValueKind != JsonValueKind.Object)
            return;

        var fiveHour = ReadUsageWindow(limits, "five_hour", "5-hour");
        var sevenDay = ReadUsageWindow(limits, "seven_day", "Weekly");

        // Absent means "nothing known" (an answer served from a cache), not
        // "no model windows", so the previous list stands.
        IReadOnlyList<PlanLimitWindow>? models = null;
        if (limits.TryGetProperty("model_scoped", out var scoped) && scoped.ValueKind == JsonValueKind.Array)
        {
            var list = new List<PlanLimitWindow>();
            foreach (var row in scoped.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var name = row.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (ReadNumber(row, "utilization") is not { } percent) continue;
                list.Add(new PlanLimitWindow(name!, percent, ReadIsoTime(row, "resets_at")));
            }
            models = list;
        }

        Update(fiveHour, sevenDay, models, keepMissingWindows: false);
    }

    /// <summary>
    /// Applies the <c>rate_limit_info</c> of a <c>rate_limit_event</c>.
    ///
    /// CLI 2.1.296 sends both windows under <c>unifiedWindows</c>, as a fraction
    /// of 1 and a Unix time. That field is not in the SDK types, so the
    /// documented single-window form (<c>rateLimitType</c>, <c>utilization</c>,
    /// <c>resetsAt</c>) is read when it is missing. A window this event does not
    /// mention keeps its previous reading.
    /// </summary>
    public void ApplyRateLimitEvent(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object) return;

        PlanLimitWindow? fiveHour = null, sevenDay = null;

        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            fiveHour = ReadEventWindow(windows, "five_hour", "5-hour");
            sevenDay = ReadEventWindow(windows, "seven_day", "Weekly");
        }
        else
        {
            var type = info.TryGetProperty("rateLimitType", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            var fraction = ReadNumber(info, "utilization");
            // A rejected request means the window is full, whether or not the
            // event says by how much.
            if (fraction is null
                && info.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() == "rejected")
                fraction = 1d;

            if (fraction is { } f)
            {
                var resetsAt = ReadUnixTime(info, "resetsAt");
                if (type == "five_hour") fiveHour = new PlanLimitWindow("5-hour", f * 100d, resetsAt);
                else if (type == "seven_day") sevenDay = new PlanLimitWindow("Weekly", f * 100d, resetsAt);
            }
        }

        Update(fiveHour, sevenDay, models: null, keepMissingWindows: true);
    }

    /// <summary>
    /// Replaces the readings in one step, so two sessions reporting at once
    /// cannot interleave. A null <paramref name="models"/> keeps the current
    /// list; <paramref name="keepMissingWindows"/> does the same for a null
    /// 5-hour or weekly window.
    /// </summary>
    private void Update(
        PlanLimitWindow? fiveHour, PlanLimitWindow? sevenDay,
        IReadOnlyList<PlanLimitWindow>? models, bool keepMissingWindows)
    {
        lock (_gate)
        {
            if (keepMissingWindows)
            {
                fiveHour ??= _fiveHour;
                sevenDay ??= _sevenDay;
            }
            models ??= _models;

            if (Equals(_fiveHour, fiveHour) && Equals(_sevenDay, sevenDay) && SameList(_models, models))
                return;

            _fiveHour = fiveHour;
            _sevenDay = sevenDay;
            _models = models;
        }

        try { Changed?.Invoke(); }
        catch (Exception)
        {
            // A failing meter must not take down whichever session's dispatcher
            // happened to deliver the reading.
        }
    }

    private static bool SameList(IReadOnlyList<PlanLimitWindow> a, IReadOnlyList<PlanLimitWindow> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    private static PlanLimitWindow? ReadUsageWindow(JsonElement limits, string key, string name)
    {
        if (!limits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return ReadNumber(w, "utilization") is { } percent
            ? new PlanLimitWindow(name, percent, ReadIsoTime(w, "resets_at"))
            : null;
    }

    private static PlanLimitWindow? ReadEventWindow(JsonElement windows, string key, string name)
    {
        if (!windows.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return ReadNumber(w, "utilization") is { } fraction
            ? new PlanLimitWindow(name, fraction * 100d, ReadUnixTime(w, "resetsAt"))
            : null;
    }

    private static double? ReadNumber(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v)
            ? v
            : null;

    private static DateTimeOffset? ReadIsoTime(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    private static DateTimeOffset? ReadUnixTime(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number
            || !p.TryGetInt64(out var seconds))
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// <summary>A snapshot of <see cref="PlanUsageStore"/>.</summary>
public sealed record PlanLimits(
    PlanLimitWindow? FiveHour,
    PlanLimitWindow? SevenDay,
    IReadOnlyList<PlanLimitWindow> Models);
