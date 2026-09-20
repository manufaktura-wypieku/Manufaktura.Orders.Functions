using Manufaktura.Orders.Functions.Services;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class EmptyOrderScheduleTests
{
    [Fact]
    public void WinterTwoAmUtc_Runs()
    {
        var utc = new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero);

        Assert.True(EmptyOrderSchedule.ShouldRun(utc, pastDue: false));
        Assert.Equal(new DateOnly(2026, 1, 15), EmptyOrderSchedule.UkDate(utc));
    }

    [Fact]
    public void WinterOneAmUtc_DoesNotRun()
    {
        var utc = new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.Zero);

        Assert.False(EmptyOrderSchedule.ShouldRun(utc, pastDue: false));
    }

    [Fact]
    public void SummerOneAmUtc_RunsAsTwoAmLondon()
    {
        var utc = new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);

        Assert.True(EmptyOrderSchedule.ShouldRun(utc, pastDue: false));
        Assert.Equal(new DateOnly(2026, 7, 15), EmptyOrderSchedule.UkDate(utc));
    }

    [Fact]
    public void SummerTwoAmUtc_DoesNotRun()
    {
        var utc = new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero);

        Assert.False(EmptyOrderSchedule.ShouldRun(utc, pastDue: false));
        Assert.True(EmptyOrderSchedule.ShouldRun(utc, pastDue: true));
    }

    [Fact]
    public void AutumnClockChange_RunsAtTheSingleTwoAm()
    {
        var oneAmGmt = new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);
        var twoAmGmt = new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.Zero);

        Assert.False(EmptyOrderSchedule.ShouldRun(oneAmGmt, pastDue: false));
        Assert.True(EmptyOrderSchedule.ShouldRun(twoAmGmt, pastDue: false));
        Assert.Equal(new DateOnly(2026, 10, 25), EmptyOrderSchedule.UkDate(twoAmGmt));
    }

    [Fact]
    public void SpringClockChange_RunsOnce()
    {
        var twoAmLondon = new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);
        var threeAmLondon = new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero);

        Assert.True(EmptyOrderSchedule.ShouldRun(twoAmLondon, pastDue: false));
        Assert.False(EmptyOrderSchedule.ShouldRun(threeAmLondon, pastDue: false));
    }

    [Fact]
    public void ManualRun_IgnoresTheClock()
    {
        var midMorning = new DateTimeOffset(2026, 9, 18, 9, 30, 0, TimeSpan.Zero);

        Assert.True(EmptyOrderSchedule.ShouldRun(midMorning, pastDue: false));
        Assert.Equal(new DateOnly(2026, 9, 18), EmptyOrderSchedule.UkDate(midMorning));
    }

    [Fact]
    public void RunOutsideTheUtcTicks_IsARetry()
    {
        var late = new DateTimeOffset(2026, 7, 15, 4, 0, 0, TimeSpan.Zero);

        Assert.True(EmptyOrderSchedule.ShouldRun(late, pastDue: false));
    }

    [Fact]
    public void DelayedWinterOneAmUtc_DoesNotRun()
    {
        var late = new DateTimeOffset(2026, 1, 15, 1, 5, 0, TimeSpan.Zero);

        Assert.False(EmptyOrderSchedule.ShouldRun(late, pastDue: false));
        Assert.False(EmptyOrderSchedule.ShouldRun(late, pastDue: true));
    }

    [Fact]
    public void DelayedSummerTwoAmUtc_RunsOnlyWhenPastDue()
    {
        var late = new DateTimeOffset(2026, 7, 15, 2, 5, 0, TimeSpan.Zero);

        Assert.False(EmptyOrderSchedule.ShouldRun(late, pastDue: false));
        Assert.True(EmptyOrderSchedule.ShouldRun(late, pastDue: true));
    }
}
