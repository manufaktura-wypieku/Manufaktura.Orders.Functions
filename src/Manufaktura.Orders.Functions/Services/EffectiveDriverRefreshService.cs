using System.Net;
using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Services;

public class EffectiveDriverRefreshService : IEffectiveDriverRefresher
{
    public const int PageSize = 500;
    public const int MaximumPages = 40;

    private readonly IDataverseService _dataverse;
    private readonly IEffectiveDriverResolver _resolver;
    private readonly IDeliveryPackRegenerator _regenerator;
    private readonly ILogger<EffectiveDriverRefreshService> _logger;

    public EffectiveDriverRefreshService(
        IDataverseService dataverse,
        IEffectiveDriverResolver resolver,
        IDeliveryPackRegenerator regenerator,
        ILogger<EffectiveDriverRefreshService> logger)
    {
        _dataverse = dataverse;
        _resolver = resolver;
        _regenerator = regenerator;
        _logger = logger;
    }

    public async Task<RefreshEffectiveDriversResponse> RefreshAsync(RefreshEffectiveDriversRequest request, CancellationToken cancellationToken = default)
    {
        var (orderIds, truncated) = await GetOrderIdsAsync(request, cancellationToken);
        var distinctOrderIds = orderIds.Distinct().ToArray();
        var results = new List<RefreshEffectiveDriverOrderResult>();

        foreach (var orderId in distinctOrderIds)
            results.Add(await RefreshOrderAsync(orderId, cancellationToken));

        return new RefreshEffectiveDriversResponse(
            truncated ? "incomplete" : "complete",
            distinctOrderIds.Length,
            results.Count(result => result.Status == "updated"),
            results.Count(result => result.Status == "updated" && result.IsUncovered),
            results,
            results.Sum(result => result.DeliveryPacksRegenerated),
            results.Sum(result => result.LockedPacksRequiringReview));
    }

    private async Task<(IReadOnlyCollection<Guid> Ids, bool Truncated)> GetOrderIdsAsync(RefreshEffectiveDriversRequest request, CancellationToken cancellationToken)
    {
        if (request.OrderId is Guid orderId)
            return ([orderId], false);

        var accountIds = request.AccountIds?.Where(id => id != Guid.Empty).Distinct().ToArray();
        var query = new EffectiveDriverRefreshQuery(
            request.FromDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            request.ToDate,
            accountIds is { Length: > 0 } ? null : request.AccountId,
            request.RouteId,
            request.DriverId,
            request.MaxOrders ?? PageSize,
            accountIds is { Length: > 0 } ? accountIds : null);

        if (request.MaxOrders is int explicitMax)
        {
            var capped = await _dataverse.GetOrderIdsForEffectiveDriverRefreshAsync(query, cancellationToken);
            return (capped, capped.Count >= explicitMax);
        }

        var all = new List<Guid>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            var batch = await _dataverse.GetEffectiveDriverRefreshOrderPageAsync(query, page, cancellationToken);
            all.AddRange(batch);
            if (batch.Count < PageSize)
                return (all, false);
        }

        return (all, true);
    }

    private async Task<RefreshEffectiveDriverOrderResult> RefreshOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _dataverse.GetOrderDeliverySnapshotAsync(orderId, cancellationToken);
            var resolutionRequest = await _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(orderId, cancellationToken);
            var resolution = _resolver.Resolve(resolutionRequest);
            await _dataverse.UpdateOrderEffectiveDriverAsync(orderId, resolutionRequest.RouteId, resolution, cancellationToken);

            var regenerated = 0;
            var locked = 0;
            IReadOnlyList<string>? warnings = null;
            if (snapshot.HasDeliveryNotes)
            {
                var regeneration = await _regenerator.RegenerateAsync(
                    snapshot.EffectiveDriverId,
                    resolution.DriverId,
                    snapshot.DeliveryDate,
                    cancellationToken);
                regenerated = regeneration.RegeneratedPacks;
                locked = regeneration.LockedPacksRequiringReview;
                warnings = regeneration.Warnings.Count == 0 ? null : regeneration.Warnings;
            }

            return new RefreshEffectiveDriverOrderResult(
                orderId,
                "updated",
                resolution.DriverId,
                resolution.Source.ToString(),
                resolution.IsUncovered,
                null,
                regenerated,
                locked,
                warnings);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot refresh effective driver for order {OrderId}", orderId);
            return new RefreshEffectiveDriverOrderResult(orderId, "failed", null, null, false, ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Order {OrderId} disappeared before its effective driver could be refreshed", orderId);
            return new RefreshEffectiveDriverOrderResult(orderId, "failed", null, null, false, "Order was not found in Dataverse.");
        }
    }
}
