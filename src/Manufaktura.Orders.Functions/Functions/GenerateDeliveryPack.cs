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
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Upstream service error in GenerateDeliveryPack for note {NoteId}", request.DeliveryNoteId);
            return new ObjectResult(new { error = "An upstream service returned an error.", code = "upstream_error" })
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
        // Step 1: Read delivery note to get the order's effective driver and delivery date.
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
        catch (MissingDeliveryPackGroupingException ex)
        {
            _logger.LogWarning(ex, "Delivery note {NoteId} cannot determine delivery-pack grouping", noteId);
            return new ObjectResult(new { error = ex.Message, code = ex.Code })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity
            };
        }

        _logger.LogInformation("Delivery note {NoteId}: order={OrderId}, effectiveDriver={DriverId}, date={Date}", noteId, note.OrderId, note.EffectiveDriverId, note.DeliveryDate.Date);

        // Step 2: Count total delivery notes for the effective driver/date.
        // We compare total notes to notes-with-URL rather than notes to orders because:
        // - Not every inactive order necessarily has a delivery note (e.g. cancelled orders)
        // - The readiness criterion is "all existing delivery notes have been uploaded to SharePoint"
        var totalNoteCount = await _dataverse.CountTotalDeliveryNotesForDriverAndDateAsync(note.EffectiveDriverId, note.DeliveryDate, cancellationToken);

        // Step 3: Exit early if there are no delivery notes yet.
        if (totalNoteCount == 0)
        {
            _logger.LogInformation("No delivery notes found for effective driver {DriverId} on {Date}. Exiting.", note.EffectiveDriverId, note.DeliveryDate.Date);
            return new OkObjectResult(new { status = "skipped", reason = "not_all_notes_ready", notesWithUrlCount = 0, totalNoteCount });
        }

        // Step 4: Count delivery notes with a URL and exit if not all are ready.
        var notesWithUrlCount = await _dataverse.CountDeliveryNotesWithUrlForDriverAndDateAsync(note.EffectiveDriverId, note.DeliveryDate, cancellationToken);
        _logger.LogInformation("Delivery notes for effective driver {DriverId} on {Date}: {WithUrl}/{Total} have a URL", note.EffectiveDriverId, note.DeliveryDate.Date, notesWithUrlCount, totalNoteCount);

        if (notesWithUrlCount < totalNoteCount)
        {
            _logger.LogInformation("Not all delivery notes ready ({WithUrl}/{Total}). Exiting.", notesWithUrlCount, totalNoteCount);
            return new OkObjectResult(new { status = "skipped", reason = "not_all_notes_ready", notesWithUrlCount, totalNoteCount });
        }

        // Step 5: Check for an existing active delivery pack (for the concurrency guard and regeneration).
        var existingPack = await _dataverse.GetActiveDeliveryPackAsync(note.EffectiveDriverId, note.DeliveryDate, cancellationToken);

        if (existingPack is not null && existingPack.StatusCode is DeliveryPackStatus.Generating)
        {
            _logger.LogInformation("Delivery pack {PackId} is already {Status}. Exiting.", existingPack.Id, existingPack.StatusCode);
            return new OkObjectResult(new { status = "skipped", reason = "already_generating", packId = existingPack.Id });
        }

        // Step 6: Collect all delivery note document URLs before creating/updating the pack,
        //         so we can skip cleanly if no URLs are available.
        var documentUrls = await _dataverse.GetDeliveryNoteUrlsForDriverAndDateAsync(note.EffectiveDriverId, note.DeliveryDate, cancellationToken);
        _logger.LogInformation("Found {Count} delivery note document URLs for effective driver {DriverId} on {Date}.", documentUrls.Length, note.EffectiveDriverId, note.DeliveryDate.Date);

        if (documentUrls.Length == 0)
        {
            _logger.LogInformation("No document URLs found for effective driver {DriverId} on {Date}. Exiting.", note.EffectiveDriverId, note.DeliveryDate.Date);
            return new OkObjectResult(new { status = "skipped", reason = "no_document_urls" });
        }

        // Step 7: Fetch driver name for pack naming and SharePoint upload path.
        var driverName = await _dataverse.GetDriverNameAsync(note.EffectiveDriverId, cancellationToken);
        if (string.IsNullOrWhiteSpace(driverName))
            driverName = note.EffectiveDriverId.ToString("D");
        var packName = $"{driverName} - {note.DeliveryDate:yyyy-MM-dd}";

        // Step 8: Upsert active delivery pack version (create or update to Generating).
        Guid packId;

        if (existingPack is not null)
        {
            packId = existingPack.Id;
            await _dataverse.SetDeliveryPackGeneratingAsync(packId, documentUrls.Length, packName, cancellationToken);
            _logger.LogInformation("Updated existing delivery pack {PackId} to Generating with {NoteCount} notes.", packId, documentUrls.Length);
        }
        else
        {
            var (newPackId, created) = await _dataverse.CreateDeliveryPackAsync(note.EffectiveDriverId, note.DeliveryDate, documentUrls.Length, packName, cancellationToken);
            packId = newPackId;
            if (!created)
            {
                _logger.LogInformation("Delivery pack {PackId} was created by a concurrent request. Skipping duplicate generation.", newPackId);
                return new OkObjectResult(new { status = "skipped", reason = "already_generating", packId = newPackId });
            }
            _logger.LogInformation("Created new delivery pack {PackId} for effective driver {DriverId} on {Date}.", packId, note.EffectiveDriverId, note.DeliveryDate.Date);
        }

        try
        {
            // Step 9: Merge documents into a single PDF.
            _logger.LogInformation("Merging {Count} delivery note documents for pack {PackId}.", documentUrls.Length, packId);
            var pdfBytes = await _mergeService.MergeDocumentsAsync(documentUrls, cancellationToken);
            _logger.LogInformation("Merge complete: {Size} bytes for pack {PackId}.", pdfBytes.Length, packId);

            // Step 10: Upload merged PDF to SharePoint /DeliveryPacks/{DriverName}/
            var sharePointUrl = await _sharePoint.UploadDeliveryPackAsync(driverName, note.DeliveryDate, pdfBytes, cancellationToken);
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
