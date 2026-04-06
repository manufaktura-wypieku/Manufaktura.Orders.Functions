using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Manufaktura.Orders.Functions.Functions;

public class GenerateDeliveryPack
{
    private readonly IDataverseService _dataverse;
    private readonly IDocumentMergeService _mergeService;
    private readonly ISharePointService _sharePoint;
    private readonly ILogger<GenerateDeliveryPack> _logger;

    public GenerateDeliveryPack(
        IDataverseService dataverse,
        IDocumentMergeService mergeService,
        ISharePointService sharePoint,
        ILogger<GenerateDeliveryPack> logger)
    {
        _dataverse = dataverse;
        _mergeService = mergeService;
        _sharePoint = sharePoint;
        _logger = logger;
    }

    [Function("GenerateDeliveryPack")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        GenerateDeliveryPackRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<GenerateDeliveryPackRequest>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize GenerateDeliveryPack request body");
            return new BadRequestObjectResult(new { error = "Request body contains invalid JSON.", code = "invalid_json" });
        }
        catch (InvalidOperationException ex)
        {
            // ReadFromJsonAsync throws InvalidOperationException when the Content-Type is missing
            // or is not a supported JSON media type.
            _logger.LogWarning(ex, "GenerateDeliveryPack request has unsupported or missing Content-Type");
            return new ObjectResult(new { error = "Request Content-Type must be application/json.", code = "unsupported_media_type" })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType
            };
        }

        if (request is null || request.DeliveryNoteId == Guid.Empty)
        {
            _logger.LogWarning("Received GenerateDeliveryPack request with missing or empty DeliveryNoteId");
            return new BadRequestObjectResult(new { error = "DeliveryNoteId is required.", code = "missing_delivery_note_id" });
        }

        try
        {
            return await RunOrchestrationAsync(request.DeliveryNoteId, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(ex, "Upstream auth failure in GenerateDeliveryPack for note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = "Upstream service access denied.", code = "upstream_auth_failure" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in GenerateDeliveryPack for note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = "An unexpected error occurred.", code = "internal_error" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private async Task<IActionResult> RunOrchestrationAsync(Guid noteId, CancellationToken cancellationToken)
    {
        // Step 1: Read delivery note to get route and delivery date.
        _logger.LogInformation("Reading delivery note {NoteId}", noteId);
        DeliveryNoteRecord note;
        try
        {
            note = await _dataverse.GetDeliveryNoteAsync(noteId, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Delivery note {NoteId} not found in Dataverse", noteId);
            return new NotFoundObjectResult(new { error = "Delivery note not found.", code = "delivery_note_not_found" });
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(ex, "Unauthorized access to Dataverse for delivery note {NoteId}", noteId);
            return new ObjectResult(new { error = "Upstream service access denied.", code = "upstream_auth_failure" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }

        _logger.LogInformation("Delivery note {NoteId}: route={RouteId}, date={Date}", noteId, note.RouteId, note.DeliveryDate.Date);

        // Step 2: Count completed orders for the route/date.
        var orderCount = await _dataverse.CountCompletedOrdersByRouteAndDateAsync(note.RouteId, note.DeliveryDate, cancellationToken);
        _logger.LogInformation("Completed orders for route {RouteId} on {Date}: {Count}", note.RouteId, note.DeliveryDate.Date, orderCount);

        // Step 3: Exit early if there are no completed orders — avoids unnecessary Dataverse calls.
        if (orderCount == 0)
        {
            _logger.LogInformation("No completed orders for route {RouteId} on {Date}. Exiting.", note.RouteId, note.DeliveryDate.Date);
            return new OkObjectResult(new { status = "skipped", reason = "no_orders", orderCount });
        }

        // Step 4: Count delivery notes with a SharePoint URL for the route/date.
        var noteCount = await _dataverse.CountDeliveryNotesWithUrlAsync(note.RouteId, note.DeliveryDate, cancellationToken);
        _logger.LogInformation("Delivery notes with URL for route {RouteId} on {Date}: {Count}", note.RouteId, note.DeliveryDate.Date, noteCount);

        // Step 5: Exit if not all delivery notes are ready.
        if (noteCount < orderCount)
        {
            _logger.LogInformation("Not all delivery notes ready ({NoteCount}/{OrderCount}). Exiting.", noteCount, orderCount);
            return new OkObjectResult(new { status = "skipped", reason = "not_all_notes_ready", noteCount, orderCount });
        }

        // Step 6: Check for an existing delivery pack (for the concurrency guard).
        var existingPack = await _dataverse.GetDeliveryPackAsync(note.RouteId, note.DeliveryDate, cancellationToken);

        if (existingPack is not null && existingPack.StatusCode == DeliveryPackStatus.Generating)
        {
            _logger.LogInformation("Delivery pack {PackId} is already Generating. Exiting.", existingPack.Id);
            return new OkObjectResult(new { status = "skipped", reason = "already_generating", packId = existingPack.Id });
        }

        // Step 7: Collect all delivery note document URLs before creating/updating the pack,
        //         so we can skip cleanly if no URLs are available.
        var documentUrls = await _dataverse.GetDeliveryNoteUrlsAsync(note.RouteId, note.DeliveryDate, cancellationToken);
        _logger.LogInformation("Found {Count} delivery note document URLs for route {RouteId} on {Date}.", documentUrls.Length, note.RouteId, note.DeliveryDate.Date);

        if (documentUrls.Length == 0)
        {
            _logger.LogInformation("No document URLs found for route {RouteId} on {Date}. Exiting.", note.RouteId, note.DeliveryDate.Date);
            return new OkObjectResult(new { status = "skipped", reason = "no_document_urls" });
        }

        // Step 8: Upsert delivery pack (create or update to Generating).
        Guid packId;

        if (existingPack is not null)
        {
            packId = existingPack.Id;
            await _dataverse.SetDeliveryPackGeneratingAsync(packId, documentUrls.Length, cancellationToken);
            _logger.LogInformation("Updated existing delivery pack {PackId} to Generating with {NoteCount} notes.", packId, documentUrls.Length);
        }
        else
        {
            var (newPackId, created) = await _dataverse.CreateDeliveryPackAsync(note.RouteId, note.DeliveryDate, documentUrls.Length, cancellationToken);
            packId = newPackId;
            if (!created)
            {
                _logger.LogInformation("Delivery pack {PackId} was created by a concurrent request. Skipping duplicate generation.", newPackId);
                return new OkObjectResult(new { status = "skipped", reason = "already_generating", packId = newPackId });
            }
            _logger.LogInformation("Created new delivery pack {PackId} for route {RouteId} on {Date}.", packId, note.RouteId, note.DeliveryDate.Date);
        }

        try
        {
            // Step 9: Merge documents into a single PDF.
            _logger.LogInformation("Merging {Count} delivery note documents for pack {PackId}.", documentUrls.Length, packId);
            var pdfBytes = await _mergeService.MergeDocumentsAsync(documentUrls, cancellationToken);
            _logger.LogInformation("Merge complete: {Size} bytes for pack {PackId}.", pdfBytes.Length, packId);

            // Step 10: Upload merged PDF to SharePoint /DeliveryPacks/{RouteName}/
            var routeName = await _dataverse.GetDeliveryRouteNameAsync(note.RouteId, cancellationToken);
            if (string.IsNullOrWhiteSpace(routeName))
                routeName = note.RouteId.ToString("D");
            var sharePointUrl = await _sharePoint.UploadDeliveryPackAsync(routeName, note.DeliveryDate, pdfBytes, cancellationToken);
            _logger.LogInformation("Uploaded delivery pack PDF to {Url} for pack {PackId}.", sharePointUrl, packId);

            // Step 11: Update delivery pack record to Complete.
            await _dataverse.UpdateDeliveryPackCompleteAsync(packId, sharePointUrl, documentUrls.Length, DateTimeOffset.UtcNow, cancellationToken);
            _logger.LogInformation("Delivery pack {PackId} marked Complete.", packId);

            return new OkObjectResult(new
            {
                status = "complete",
                packId,
                url = sharePointUrl,
                mergedCount = documentUrls.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating delivery pack {PackId}", packId);
            await TryMarkFailedAsync(packId, ex, cancellationToken);

            return new ObjectResult(new { error = "Failed to generate delivery pack.", code = "generation_error", packId })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private async Task TryMarkFailedAsync(Guid packId, Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            var log = $"[{DateTimeOffset.UtcNow:O}] {ex.GetType().Name}: {ex.Message}";
            await _dataverse.UpdateDeliveryPackFailedAsync(packId, log, cancellationToken);
        }
        catch (Exception updateEx)
        {
            _logger.LogError(updateEx, "Failed to mark delivery pack {PackId} as Failed", packId);
        }
    }
}
