namespace Manufaktura.Orders.Functions.Models;

public class RefreshEffectiveDriversRequest
{
    public Guid? OrderId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? RouteId { get; init; }
    public Guid? DriverId { get; init; }
    public DateOnly? FromDate { get; init; }
    public DateOnly? ToDate { get; init; }
    public int? MaxOrders { get; init; }
}

public record EffectiveDriverRefreshQuery(
    DateOnly FromDate,
    DateOnly? ToDate,
    Guid? AccountId,
    Guid? RouteId,
    Guid? DriverId,
    int MaxOrders);

public record RefreshEffectiveDriversResponse(
    string Status,
    int MatchedOrders,
    int UpdatedOrders,
    int UncoveredOrders,
    IReadOnlyCollection<RefreshEffectiveDriverOrderResult> Results);

public record RefreshEffectiveDriverOrderResult(
    Guid OrderId,
    string Status,
    Guid? DriverId,
    string? Source,
    bool IsUncovered,
    string? Error);
