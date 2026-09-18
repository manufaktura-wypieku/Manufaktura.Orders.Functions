using System.Net;
using System.Text.Json;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Functions;

public class ResolveEffectiveDriver
{
    private readonly IDataverseService _dataverse;
    private readonly IEffectiveDriverResolver _resolver;
    private readonly ILogger<ResolveEffectiveDriver> _logger;

    public ResolveEffectiveDriver(
        IDataverseService dataverse,
        IEffectiveDriverResolver resolver,
        ILogger<ResolveEffectiveDriver> logger)
    {
        _dataverse = dataverse;
        _resolver = resolver;
        _logger = logger;
    }

    [Function("ResolveEffectiveDriver")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        ResolveEffectiveDriverRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<ResolveEffectiveDriverRequest>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize ResolveEffectiveDriver request body");
            return new BadRequestObjectResult(new { error = "Request body contains invalid JSON.", code = "invalid_json" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ResolveEffectiveDriver request has unsupported or missing Content-Type");
            return new ObjectResult(new { error = "Request Content-Type must be application/json.", code = "unsupported_media_type" })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType
            };
        }

        if (request is null || request.OrderId == Guid.Empty)
        {
            _logger.LogWarning("Received ResolveEffectiveDriver request with missing or empty OrderId");
            return new BadRequestObjectResult(new { error = "OrderId is required.", code = "missing_order_id" });
        }

        try
        {
            var resolutionRequest = await _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(request.OrderId, cancellationToken);
            var resolution = _resolver.Resolve(resolutionRequest);

            return new OkObjectResult(new ResolveEffectiveDriverResponse(
                request.OrderId,
                resolution.DriverId,
                resolution.Source.ToString(),
                resolution.IsUncovered));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Order {OrderId} not found in Dataverse", request.OrderId);
            return new NotFoundObjectResult(new { error = "Order not found.", code = "order_not_found" });
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(ex, "Unauthorized access to Dataverse for order {OrderId}", request.OrderId);
            return new ObjectResult(new { error = "Upstream service access denied.", code = "upstream_auth_failure" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Cannot resolve effective driver for order {OrderId}", request.OrderId);
            return new ObjectResult(new { error = ex.Message, code = "invalid_order_driver_context" })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Upstream service error in ResolveEffectiveDriver for order {OrderId}", request.OrderId);
            return new ObjectResult(new { error = "An upstream service returned an error.", code = "upstream_error" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in ResolveEffectiveDriver for order {OrderId}", request.OrderId);
            return new ObjectResult(new { error = "An unexpected error occurred.", code = "internal_error" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}
