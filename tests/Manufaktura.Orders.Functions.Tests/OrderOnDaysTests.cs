using Manufaktura.Orders.Functions.Services;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class OrderOnDaysTests
{
    [Theory]
    [InlineData(DayOfWeek.Sunday, 3)]
    [InlineData(DayOfWeek.Monday, 4)]
    [InlineData(DayOfWeek.Tuesday, 5)]
    [InlineData(DayOfWeek.Wednesday, 6)]
    [InlineData(DayOfWeek.Thursday, 7)]
    [InlineData(DayOfWeek.Friday, 8)]
    [InlineData(DayOfWeek.Saturday, 9)]
    public void MapsCalendarDayToSameWeekdayOption(DayOfWeek day, int optionValue)
    {
        Assert.Equal(optionValue, OrderOnDays.For(day));
    }
}
