namespace Manufaktura.Orders.Functions.Services;

/// <summary>
/// Decides when a scheduled invocation is the 02:00 Europe/London run.
/// The timer itself fires at 01:00 and 02:00 UTC, which together cover 02:00 London across daylight saving.
/// 02:00 London happens once, including the autumn change, because the repeated hour is 01:00.
/// </summary>
internal static class EmptyOrderSchedule
{
    private static readonly TimeZoneInfo London = ResolveLondon();

    public static DateTimeOffset UkLocal(DateTimeOffset utcNow)
        => TimeZoneInfo.ConvertTime(utcNow, London);

    public static bool ShouldRun(DateTimeOffset utcNow, bool pastDue)
    {
        var localHour = UkLocal(utcNow).Hour;
        if (localHour == 2)
            return true;

        if (pastDue && localHour > 2)
            return true;

        // The timer is due at 01:00 and 02:00 UTC. A late tick in those hours is still that
        // schedule, not a manual retry, even when the minute is no longer 0.
        var isScheduledTick = utcNow.Hour is 1 or 2;
        return !isScheduledTick;
    }

    public static DateOnly UkDate(DateTimeOffset utcNow)
        => DateOnly.FromDateTime(UkLocal(utcNow).DateTime);

    private static TimeZoneInfo ResolveLondon()
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                continue;
            }
            catch (InvalidTimeZoneException)
            {
                continue;
            }
        }

        throw new TimeZoneNotFoundException("Neither Europe/London nor GMT Standard Time is available.");
    }
}
