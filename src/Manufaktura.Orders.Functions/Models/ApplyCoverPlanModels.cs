namespace Manufaktura.Orders.Functions.Models;

public class ApplyCoverPlanRequest
{
    public Guid DriverId { get; init; }
    public DateOnly FromDate { get; init; }
    public DateOnly ToDate { get; init; }
    public Guid[] AccountIds { get; init; } = [];
    public string? DriverName { get; init; }
    public Dictionary<string, string>? AccountNames { get; init; }
}

public record ApplyCoverPlanResponse(
    string Status,
    int CreatedCount,
    int AlreadyCoveredCount,
    int EffectiveDriversRefreshedCount,
    int DeliveryPacksRegeneratedCount,
    int LockedPacksRequiringReviewCount,
    IReadOnlyList<string> Warnings);
