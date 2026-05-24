using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class EffectiveDriverResolverTests
{
    private static readonly Guid AccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid DefaultDriverId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WeekdayDriverId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OverrideDriverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly DateOnly DeliveryDate = new(2026, 5, 8);

    private readonly EffectiveDriverResolver _resolver = new();

    [Fact]
    public void Resolve_UsesAccountOverrideBeforeRouteRotaAndAbsence()
    {
        var request = CreateRequest(
            accountOverrides:
            [
                new AccountDeliveryOverrideRecord(AccountId, DeliveryDate.AddDays(-1), DeliveryDate.AddDays(1), OverrideDriverId)
            ],
            driverAbsences:
            [
                new DriverAbsenceRecord(WeekdayDriverId, DeliveryDate, DeliveryDate)
            ]);

        var result = _resolver.Resolve(request);

        Assert.Equal(OverrideDriverId, result.DriverId);
        Assert.Equal(EffectiveDriverSource.AccountOverride, result.Source);
        Assert.False(result.IsUncovered);
    }

    [Fact]
    public void Resolve_UsesWeekdayDriverWhenConfigured()
    {
        var request = CreateRequest();

        var result = _resolver.Resolve(request);

        Assert.Equal(WeekdayDriverId, result.DriverId);
        Assert.Equal(EffectiveDriverSource.RouteWeekday, result.Source);
    }

    [Fact]
    public void Resolve_FallsBackToDefaultDriverWhenWeekdayDriverIsBlank()
    {
        var request = CreateRequest(includeWeekdayDriver: false);

        var result = _resolver.Resolve(request);

        Assert.Equal(DefaultDriverId, result.DriverId);
        Assert.Equal(EffectiveDriverSource.RouteDefault, result.Source);
    }

    [Fact]
    public void Resolve_ReturnsUncoveredWhenBaselineDriverIsAbsent()
    {
        var request = CreateRequest(
            driverAbsences:
            [
                new DriverAbsenceRecord(WeekdayDriverId, DeliveryDate.AddDays(-2), DeliveryDate.AddDays(2))
            ]);

        var result = _resolver.Resolve(request);

        Assert.Null(result.DriverId);
        Assert.Equal(EffectiveDriverSource.UncoveredDueToDriverAbsence, result.Source);
        Assert.True(result.IsUncovered);
    }

    [Fact]
    public void Resolve_ReturnsMissingRouteDriverWhenNoBaselineDriverExists()
    {
        var request = CreateRequest(includeDefaultDriver: false, includeWeekdayDriver: false);

        var result = _resolver.Resolve(request);

        Assert.Null(result.DriverId);
        Assert.Equal(EffectiveDriverSource.MissingRouteDriver, result.Source);
        Assert.True(result.IsUncovered);
    }

    [Fact]
    public void Resolve_ThrowsWhenMoreThanOneAccountOverrideMatches()
    {
        var request = CreateRequest(
            accountOverrides:
            [
                new AccountDeliveryOverrideRecord(AccountId, DeliveryDate, DeliveryDate, OverrideDriverId),
                new AccountDeliveryOverrideRecord(AccountId, DeliveryDate.AddDays(-1), DeliveryDate.AddDays(1), Guid.NewGuid())
            ]);

        var exception = Assert.Throws<InvalidOperationException>(() => _resolver.Resolve(request));
        Assert.Contains("More than one account delivery override matches", exception.Message);
    }

    private static EffectiveDriverResolutionRequest CreateRequest(
        bool includeDefaultDriver = true,
        bool includeWeekdayDriver = true,
        IReadOnlyCollection<AccountDeliveryOverrideRecord>? accountOverrides = null,
        IReadOnlyCollection<DriverAbsenceRecord>? driverAbsences = null)
    {
        Guid? resolvedDefaultDriverId = includeDefaultDriver ? DefaultDriverId : null;
        Guid? resolvedWeekdayDriverId = includeWeekdayDriver ? WeekdayDriverId : null;

        var weekdayDrivers = new Dictionary<DayOfWeek, Guid?>
        {
            [DeliveryDate.DayOfWeek] = resolvedWeekdayDriverId
        };

        return new EffectiveDriverResolutionRequest(
            AccountId,
            DeliveryDate,
            new RouteDriverSchedule(resolvedDefaultDriverId, weekdayDrivers),
            accountOverrides ?? [],
            driverAbsences ?? []);
    }
}
