using System;
using VsAgentic.Services.Abstractions;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// When a plan window counts as "not reported": the CLI reported the other
/// window, so the account has plan limits, but has no reading for this one.
/// </summary>
public class SessionUsageWindowTests
{
    private static readonly PlanLimitWindow FiveHour = new("5-hour", 55, DateTimeOffset.UtcNow.AddHours(2));
    private static readonly PlanLimitWindow Weekly = new("Weekly", 51, DateTimeOffset.UtcNow.AddDays(1));

    [Fact]
    public void Weekly_only_leaves_the_5_hour_window_not_reported()
    {
        var usage = new SessionUsage { LongWindowLimit = Weekly };

        Assert.True(usage.ShortWindowNotReported);
        Assert.False(usage.LongWindowNotReported);
    }

    [Fact]
    public void Five_hour_only_leaves_the_weekly_window_not_reported()
    {
        var usage = new SessionUsage { ShortWindowLimit = FiveHour };

        Assert.False(usage.ShortWindowNotReported);
        Assert.True(usage.LongWindowNotReported);
    }

    [Fact]
    public void No_plan_limits_keep_the_estimate()
    {
        var usage = new SessionUsage();

        Assert.False(usage.ShortWindowNotReported);
        Assert.False(usage.LongWindowNotReported);
    }

    [Fact]
    public void Both_windows_reported()
    {
        var usage = new SessionUsage { ShortWindowLimit = FiveHour, LongWindowLimit = Weekly };

        Assert.False(usage.ShortWindowNotReported);
        Assert.False(usage.LongWindowNotReported);
    }
}
