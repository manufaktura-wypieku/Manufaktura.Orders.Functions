using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Manufaktura.Orders.Functions.Functions;

public class MergeDeliveryNotes
{
    private readonly IDocumentMergeService _mergeService;
    private readonly ILogger<MergeDeliveryNotes> _logger;

    public MergeDeliveryNotes(IDocumentMergeService mergeService, ILogger<MergeDeliveryNotes> logger)
    {
        _mergeService = mergeService;
        _logger = logger;
    }

    [Function("MergeDeliveryNotes")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        MergeRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<MergeRequest>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize merge request body");
            return new BadRequestObjectResult(new { error = $"Request body contains invalid JSON: {ex.Message}" });
        }

        if (request?.DocumentUrls is null || request.DocumentUrls.Length == 0)
        {
            _logger.LogWarning("Received merge request with no document URLs");
            return new BadRequestObjectResult(new { error = "documentUrls array is required and must not be empty." });
        }

        _logger.LogInformation("Merging {Count} delivery note documents", request.DocumentUrls.Length);

        try
        {
            var pdfBytes = await _mergeService.MergeDocumentsAsync(request.DocumentUrls, cancellationToken);

            _logger.LogInformation("Merge complete, output PDF is {Size} bytes", pdfBytes.Length);

            return new FileContentResult(pdfBytes, "application/pdf")
            {
                FileDownloadName = "DeliveryPack.pdf"
            };
        }
        catch (System.UriFormatException ex)
        {
            _logger.LogWarning(ex, "Received merge request with invalid document URL format");
            return new BadRequestObjectResult(new
            {
                error = "One or more documentUrls values are invalid.",
                code = "invalid_document_urls"
            });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Received merge request with invalid document URL input");
            return new BadRequestObjectResult(new
            {
                error = "One or more documentUrls values are invalid.",
                code = "invalid_document_urls"
            });
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch one or more documents for merge");
            return new ObjectResult(new
            {
                error = "Failed to fetch one or more source documents.",
                code = "document_fetch_failed"
            })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }
}
