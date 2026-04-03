using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

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
        var request = await req.ReadFromJsonAsync<MergeRequest>(cancellationToken);

        if (request?.DocumentUrls is null || request.DocumentUrls.Length == 0)
        {
            _logger.LogWarning("Received merge request with no document URLs");
            return new BadRequestObjectResult(new { error = "documentUrls array is required and must not be empty." });
        }

        _logger.LogInformation("Merging {Count} delivery note documents", request.DocumentUrls.Length);

        var pdfBytes = await _mergeService.MergeDocumentsAsync(request.DocumentUrls, cancellationToken);

        _logger.LogInformation("Merge complete, output PDF is {Size} bytes", pdfBytes.Length);

        return new FileContentResult(pdfBytes, "application/pdf")
        {
            FileDownloadName = "DeliveryPack.pdf"
        };
    }
}
