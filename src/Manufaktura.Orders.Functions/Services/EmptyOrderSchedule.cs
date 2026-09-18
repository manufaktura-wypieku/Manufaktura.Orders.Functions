namespace Manufaktura.Orders.Functions.Services;

/// <summary>
/// Decides when a scheduled invocation is the 02:00 Europe/London run.
/// The timer itself fires at 01:00 and 02:00 UTC, which together cover 02:00 London across daylight saving.
/// 02:00 London happens once, including the autumn change, because the repeated hour is 01:00.
/// </summary>
internal static class EmptyOrderSchedule
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static bool ShouldRun(DateTimeOffset utcNow, bool pastDue)
    {
        var localHour = TimeZoneInfo.ConvertTime(utcNow, London).Hour;
        if (localHour == 2)
            return true;

        if (pastDue && localHour > 2)
            return true;

        // The timer fires at 01:00 and 02:00 UTC. Any other invocation is a same-day retry from Azure.
        var isScheduledTick = utcNow.Minute == 0 && utcNow.Hour is 1 or 2;
        return !isScheduledTick;
    }

    public static DateOnly UkDate(DateTimeOffset utcNow)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, London).DateTime);
}
