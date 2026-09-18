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
    private const int MaximumMaxOrders = 500;

    private readonly IEffectiveDriverRefresher _refresher;
    private readonly ILogger<RefreshEffectiveDrivers> _logger;

    public RefreshEffectiveDrivers(IEffectiveDriverRefresher refresher, ILogger<RefreshEffectiveDrivers> logger)
    {
        _refresher = refresher;
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
            return new OkObjectResult(await _refresher.RefreshAsync(request, cancellationToken));
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

        if (request.AccountIds?.Any(id => id == Guid.Empty) == true)
            return new BadRequestObjectResult(new { error = "Guid filters must not be empty.", code = "empty_guid_filter" });

        if (request.MaxOrders is <= 0 or > MaximumMaxOrders)
            return new BadRequestObjectResult(new { error = $"MaxOrders must be between 1 and {MaximumMaxOrders}.", code = "invalid_max_orders" });

        if (request.FromDate is not null && request.ToDate is not null && request.FromDate > request.ToDate)
            return new BadRequestObjectResult(new { error = "FromDate must be on or before ToDate.", code = "invalid_date_range" });

        return null;
    }
}
