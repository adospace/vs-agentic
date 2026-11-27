using System;
using System.IO;
using System.Text.Json;
using VsAgentic.Services.ClaudeCli;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// The plan limits saved across a Visual Studio restart. Each "new store" on
/// the same path stands for a new Visual Studio process.
/// </summary>
public sealed class PlanUsageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsagentic-plan-limits-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_dir, "plan-limits.json");

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Iso(DateTimeOffset t) => t.ToString("o");

    private static JsonElement UsageResponse(double five, DateTimeOffset? fiveResets, double seven, DateTimeOffset? sevenResets) =>
        Json($$"""
        {
          "rate_limits_available": true,
          "rate_limits": {
            "five_hour": { "utilization": {{five}}{{(fiveResets is { } f ? $", \"resets_at\": \"{Iso(f)}\"" : "")}} },
            "seven_day": { "utilization": {{seven}}{{(sevenResets is { } s ? $", \"resets_at\": \"{Iso(s)}\"" : "")}} },
            "model_scoped": [ { "display_name": "Fable", "utilization": 12, "resets_at": "{{Iso(Now.AddDays(3))}}" } ]
          }
        }
        """);

    [Fact]
    public void Reading_is_shown_after_restart()
    {
        new PlanUsageStore(PathName).ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));

        var limits = new PlanUsageStore(PathName).Read(Now);

        Assert.Equal(55, limits.FiveHour!.Percent);
        Assert.Equal("5-hour", limits.FiveHour.Name);
        Assert.Equal(Now.AddHours(2).ToUnixTimeSeconds(), limits.FiveHour.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(48, limits.SevenDay!.Percent);
        Assert.Equal("Fable", Assert.Single(limits.Models).Name);
    }

    [Fact]
    public void Saved_window_is_dropped_after_its_reset_time()
    {
        new PlanUsageStore(PathName).ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));

        var limits = new PlanUsageStore(PathName).Read(Now.AddHours(3));

        Assert.Null(limits.FiveHour);
        Assert.Equal(48, limits.SevenDay!.Percent);
    }

    [Fact]
    public void Saved_window_without_reset_time_is_not_restored()
    {
        var first = new PlanUsageStore(PathName);
        first.ApplyUsageResponse(UsageResponse(55, null, 48, Now.AddDays(4)));
        Assert.Equal(55, first.Read(Now).FiveHour!.Percent);

        var limits = new PlanUsageStore(PathName).Read(Now);

        Assert.Null(limits.FiveHour);
        Assert.Equal(48, limits.SevenDay!.Percent);
    }

    [Fact]
    public void Sign_in_without_plan_limits_clears_the_saved_reading()
    {
        new PlanUsageStore(PathName).ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));
        new PlanUsageStore(PathName).ApplyUsageResponse(Json("""{ "rate_limits_available": false }"""));

        var limits = new PlanUsageStore(PathName).Read(Now);

        Assert.Null(limits.FiveHour);
        Assert.Null(limits.SevenDay);
        Assert.Empty(limits.Models);
    }

    [Fact]
    public void Rate_limit_event_is_saved_and_keeps_the_other_window()
    {
        new PlanUsageStore(PathName).ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));

        var second = new PlanUsageStore(PathName);
        second.ApplyRateLimitEvent(Json($$"""
            { "rateLimitType": "five_hour", "utilization": 0.6, "resetsAt": {{Now.AddHours(2).ToUnixTimeSeconds()}} }
            """));

        var limits = new PlanUsageStore(PathName).Read(Now);

        Assert.Equal(60, limits.FiveHour!.Percent, 6);
        Assert.Equal(48, limits.SevenDay!.Percent);
    }

    [Fact]
    public void Reading_saved_by_another_instance_reaches_a_running_one()
    {
        var running = new PlanUsageStore(PathName);
        running.ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));
        Assert.Equal(55, running.Read(Now).FiveHour!.Percent);

        new PlanUsageStore(PathName).ApplyUsageResponse(UsageResponse(70, Now.AddHours(2), 50, Now.AddDays(4)));
        // Write stamps on some file systems are coarse; make the change visible.
        File.SetLastWriteTimeUtc(PathName, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(70, running.Read(Now).FiveHour!.Percent);
    }

    [Fact]
    public void Broken_file_gives_no_reading_and_no_error()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, "{ not json");

        var store = new PlanUsageStore(PathName);
        var limits = store.Read(Now);

        Assert.Null(limits.FiveHour);
        Assert.Null(limits.SevenDay);

        store.ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));
        Assert.Equal(55, new PlanUsageStore(PathName).Read(Now).FiveHour!.Percent);
    }

    [Fact]
    public void Store_without_a_path_keeps_the_reading_in_memory()
    {
        var store = new PlanUsageStore();
        store.ApplyUsageResponse(UsageResponse(55, Now.AddHours(2), 48, Now.AddDays(4)));

        Assert.Equal(55, store.Read(Now).FiveHour!.Percent);
        Assert.False(Directory.Exists(_dir));
    }
}
