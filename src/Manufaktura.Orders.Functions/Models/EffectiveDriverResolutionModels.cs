namespace Manufaktura.Orders.Functions.Models;

public enum EffectiveDriverSource
{
    AccountOverride,
    RouteWeekday,
    RouteDefault,
    UncoveredDueToDriverAbsence,
    MissingRouteDriver
}

public record AccountDeliveryOverrideRecord(Guid AccountId, DateOnly FromDate, DateOnly ToDate, Guid DriverId)
{
    public bool AppliesTo(Guid accountId, DateOnly deliveryDate)
        => AccountId == accountId && FromDate <= deliveryDate && deliveryDate <= ToDate;
}

public record DriverAbsenceRecord(Guid DriverId, DateOnly FromDate, DateOnly ToDate)
{
    public bool Covers(Guid driverId, DateOnly deliveryDate)
        => DriverId == driverId && FromDate <= deliveryDate && deliveryDate <= ToDate;
}

public record RouteDriverSchedule(Guid? DefaultDriverId, IReadOnlyDictionary<DayOfWeek, Guid?> WeekdayDriverIds)
{
    public Guid? GetWeekdayDriver(DayOfWeek dayOfWeek)
        => WeekdayDriverIds.TryGetValue(dayOfWeek, out var driverId) ? driverId : null;
}

public record EffectiveDriverResolutionRequest(
    Guid AccountId,
    DateOnly DeliveryDate,
    RouteDriverSchedule RouteSchedule,
    IReadOnlyCollection<AccountDeliveryOverrideRecord> AccountOverrides,
    IReadOnlyCollection<DriverAbsenceRecord> DriverAbsences);

public record EffectiveDriverResolutionResult(Guid? DriverId, EffectiveDriverSource Source)
{
    public bool IsUncovered => DriverId is null;
}
