using System.Net;
using System.Text.Json;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Functions;

public class RefreshEffectiveDrivers
{
    private const int DefaultMaxOrders = 200;
    private const int MaximumMaxOrders = 500;

    private readonly IDataverseService _dataverse;
    private readonly IEffectiveDriverResolver _resolver;
    private readonly ILogger<RefreshEffectiveDrivers> _logger;

    public RefreshEffectiveDrivers(
        IDataverseService dataverse,
        IEffectiveDriverResolver resolver,
        ILogger<RefreshEffectiveDrivers> logger)
    {
        _dataverse = dataverse;
        _resolver = resolver;
        _logger = logger;
    }

    [Function("RefreshEffectiveDrivers")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        RefreshEffectiveDriversRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<RefreshEffectiveDriversRequest>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize RefreshEffectiveDrivers request body");
            return new BadRequestObjectResult(new { error = "Request body contains invalid JSON.", code = "invalid_json" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "RefreshEffectiveDrivers request has unsupported or missing Content-Type");
            return new ObjectResult(new { error = "Request Content-Type must be application/json.", code = "unsupported_media_type" })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType
            };
        }

        request ??= new RefreshEffectiveDriversRequest();
        var validationError = Validate(request);
        if (validationError is not null)
            return validationError;

        try
        {
            var orderIds = await GetOrderIdsAsync(request, cancellationToken);
            var distinctOrderIds = orderIds.Distinct().ToArray();
            var results = new List<RefreshEffectiveDriverOrderResult>();

            foreach (var orderId in distinctOrderIds)
            {
                results.Add(await RefreshOrderAsync(orderId, cancellationToken));
            }

            var response = new RefreshEffectiveDriversResponse(
                "complete",
                distinctOrderIds.Length,
                results.Count(result => result.Status == "updated"),
                results.Count(result => result.Status == "updated" && result.IsUncovered),
                results);

            return new OkObjectResult(response);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(ex, "Unauthorized access to Dataverse in RefreshEffectiveDrivers");
            return new ObjectResult(new { error = "Upstream service access denied.", code = "upstream_auth_failure" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Upstream service error in RefreshEffectiveDrivers");
            return new ObjectResult(new { error = "An upstream service returned an error.", code = "upstream_error" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in RefreshEffectiveDrivers");
            return new ObjectResult(new { error = "An unexpected error occurred.", code = "internal_error" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private static ObjectResult? Validate(RefreshEffectiveDriversRequest request)
    {
        if (request.OrderId == Guid.Empty || request.AccountId == Guid.Empty || request.RouteId == Guid.Empty || request.DriverId == Guid.Empty)
            return new BadRequestObjectResult(new { error = "Guid filters must not be empty.", code = "empty_guid_filter" });

        if (request.MaxOrders is <= 0 or > MaximumMaxOrders)
            return new BadRequestObjectResult(new { error = $"MaxOrders must be between 1 and {MaximumMaxOrders}.", code = "invalid_max_orders" });

        if (request.FromDate is not null && request.ToDate is not null && request.FromDate > request.ToDate)
            return new BadRequestObjectResult(new { error = "FromDate must be on or before ToDate.", code = "invalid_date_range" });

        return null;
    }

    private async Task<IReadOnlyCollection<Guid>> GetOrderIdsAsync(RefreshEffectiveDriversRequest request, CancellationToken cancellationToken)
    {
        if (request.OrderId is Guid orderId)
            return [orderId];

        var query = new EffectiveDriverRefreshQuery(
            request.FromDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            request.ToDate,
            request.AccountId,
            request.RouteId,
            request.DriverId,
            request.MaxOrders ?? DefaultMaxOrders);

        return await _dataverse.GetOrderIdsForEffectiveDriverRefreshAsync(query, cancellationToken);
    }

    private async Task<RefreshEffectiveDriverOrderResult> RefreshOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var resolutionRequest = await _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(orderId, cancellationToken);
            var resolution = _resolver.Resolve(resolutionRequest);
            await _dataverse.UpdateOrderEffectiveDriverAsync(orderId, resolutionRequest.RouteId, resolution, cancellationToken);

            return new RefreshEffectiveDriverOrderResult(
                orderId,
                "updated",
                resolution.DriverId,
                resolution.Source.ToString(),
                resolution.IsUncovered,
                null);
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
