using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public class EffectiveDriverResolver : IEffectiveDriverResolver
{
    public EffectiveDriverResolutionResult Resolve(EffectiveDriverResolutionRequest request)
    {
        var matchingOverrides = request.AccountOverrides
            .Where(accountOverride => accountOverride.AppliesTo(request.AccountId, request.DeliveryDate))
            .ToArray();

        if (matchingOverrides.Length > 1)
        {
            throw new InvalidOperationException($"More than one account delivery override matches account '{request.AccountId:D}' on '{request.DeliveryDate:yyyy-MM-dd}'.");
        }

        if (matchingOverrides.Length == 1)
        {
            return new EffectiveDriverResolutionResult(matchingOverrides[0].DriverId, EffectiveDriverSource.AccountOverride);
        }

        var weekdayDriverId = request.RouteSchedule.GetWeekdayDriver(request.DeliveryDate.DayOfWeek);
        if (weekdayDriverId is not null)
        {
            return ResolveBaselineDriver(weekdayDriverId.Value, EffectiveDriverSource.RouteWeekday, request);
        }

        if (request.RouteSchedule.DefaultDriverId is not null)
        {
            return ResolveBaselineDriver(request.RouteSchedule.DefaultDriverId.Value, EffectiveDriverSource.RouteDefault, request);
        }

        return new EffectiveDriverResolutionResult(null, EffectiveDriverSource.MissingRouteDriver);
    }

    private static EffectiveDriverResolutionResult ResolveBaselineDriver(
        Guid driverId,
        EffectiveDriverSource source,
        EffectiveDriverResolutionRequest request)
    {
        var driverIsAbsent = request.DriverAbsences.Any(absence => absence.Covers(driverId, request.DeliveryDate));

        return driverIsAbsent
            ? new EffectiveDriverResolutionResult(null, EffectiveDriverSource.UncoveredDueToDriverAbsence)
            : new EffectiveDriverResolutionResult(driverId, source);
    }
}
