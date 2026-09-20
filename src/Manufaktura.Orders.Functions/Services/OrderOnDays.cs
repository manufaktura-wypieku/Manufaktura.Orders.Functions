namespace Manufaktura.Orders.Functions.Services;

/// <summary>
/// Maps a calendar weekday to the global choice <c>sameweekday_options</c> used by account Order on days.
/// Day (0), Weekday (1), and Weekend Day (2) are not delivery weekdays.
/// </summary>
internal static class OrderOnDays
{
    public static int For(DayOfWeek day) => (int)day + 3;
}
