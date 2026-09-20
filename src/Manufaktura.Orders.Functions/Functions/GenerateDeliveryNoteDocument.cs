using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Manufaktura.Orders.Functions.Functions;

public class GenerateDeliveryNoteDocument
{
    private readonly IDeliveryNoteDocumentGenerator _generator;
    private readonly ILogger<GenerateDeliveryNoteDocument> _logger;

    public GenerateDeliveryNoteDocument(
        IDeliveryNoteDocumentGenerator generator,
        ILogger<GenerateDeliveryNoteDocument> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    [Function("GenerateDeliveryNoteDocument")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        GenerateDeliveryNoteDocumentRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<GenerateDeliveryNoteDocumentRequest>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize GenerateDeliveryNoteDocument request body");
            return new BadRequestObjectResult(new { error = "Request body contains invalid JSON.", code = "invalid_json" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "GenerateDeliveryNoteDocument request has unsupported or missing Content-Type");
            return new ObjectResult(new { error = "Request Content-Type must be application/json.", code = "unsupported_media_type" })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType
            };
        }

        if (request is null || request.DeliveryNoteId == Guid.Empty)
        {
            _logger.LogWarning("Received GenerateDeliveryNoteDocument request with missing or empty DeliveryNoteId");
            return new BadRequestObjectResult(new { error = "DeliveryNoteId is required.", code = "missing_delivery_note_id" });
        }

        try
        {
            var result = await _generator.GenerateAsync(request.DeliveryNoteId, request.Force, cancellationToken);
            return new OkObjectResult(new
            {
                status = result.Status,
                reason = result.Reason,
                deliveryNoteId = result.DeliveryNoteId,
                url = result.Url,
                name = result.Name
            });
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Delivery note or related record not found for {NoteId}", request.DeliveryNoteId);
            return new NotFoundObjectResult(new { error = "Delivery note not found.", code = "delivery_note_not_found" });
        }
        catch (OrderItemsNotReadyException ex)
        {
            _logger.LogWarning(ex, "Order items not ready for delivery note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = ex.Message, code = "order_items_not_ready" })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(ex, "Upstream auth failure in GenerateDeliveryNoteDocument for note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = "Upstream service access denied.", code = "upstream_auth_failure" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Upstream service error in GenerateDeliveryNoteDocument for note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = "An upstream service returned an error.", code = "upstream_error" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GenerateDeliveryNoteDocument for note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = "An unexpected error occurred.", code = "internal_error" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }
}
