using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Services;

public sealed class DeliveryNoteDocumentGenerator : IDeliveryNoteDocumentGenerator
{
    public const string DefaultTemplatePath = "Templates/delivery_note_patterns_footer.docx";
    public const string DeliveryNotesFolder = "Delivery notes";

    private readonly IDataverseService _dataverse;
    private readonly ISharePointService _sharePoint;
    private readonly IDeliveryNoteWordTemplateFiller _templateFiller;
    private readonly ILogger<DeliveryNoteDocumentGenerator> _logger;
    private readonly string _templatePath;

    public DeliveryNoteDocumentGenerator(
        IDataverseService dataverse,
        ISharePointService sharePoint,
        IDeliveryNoteWordTemplateFiller templateFiller,
        IConfiguration configuration,
        ILogger<DeliveryNoteDocumentGenerator> logger)
    {
        _dataverse = dataverse;
        _sharePoint = sharePoint;
        _templateFiller = templateFiller;
        _logger = logger;
        _templatePath = configuration["DeliveryNoteTemplatePath"] ?? DefaultTemplatePath;
    }

    public async Task<DeliveryNoteDocumentGenerationResult> GenerateAsync(Guid deliveryNoteId, bool force, CancellationToken cancellationToken = default)
    {
        var source = await _dataverse.GetDeliveryNoteDocumentSourceAsync(deliveryNoteId, cancellationToken);

        if (!force && !string.IsNullOrWhiteSpace(source.ExistingUrl))
        {
            _logger.LogInformation("Delivery note {NoteId} already has mb_url; skipping.", deliveryNoteId);
            return new DeliveryNoteDocumentGenerationResult(
                Status: "skipped",
                Reason: "already_has_url",
                DeliveryNoteId: deliveryNoteId,
                Url: source.ExistingUrl);
        }

        EnsureLinesReady(source);

        var fileStem = BuildFileStem(source);
        var docxName = $"{fileStem}.docx";
        var pdfName = $"{fileStem}.pdf";
        var docxPath = $"{DeliveryNotesFolder}/{docxName}";

        _logger.LogInformation("Downloading delivery note template from {TemplatePath}", _templatePath);
        var templateBytes = await _sharePoint.DownloadFileByPathAsync(_templatePath, cancellationToken);
        var filledDocx = _templateFiller.Fill(templateBytes, source);

        _logger.LogInformation("Uploading temporary Word file {DocxPath}", docxPath);
        await _sharePoint.UploadFileAsync(
            DeliveryNotesFolder,
            docxName,
            filledDocx,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            cancellationToken);

        try
        {
            _logger.LogInformation("Converting {DocxPath} to PDF via Graph", docxPath);
            var pdfBytes = await _sharePoint.DownloadAsPdfAsync(docxPath, cancellationToken);

            _logger.LogInformation("Uploading delivery note PDF {PdfName}", pdfName);
            var pdfUrl = await _sharePoint.UploadFileAsync(
                DeliveryNotesFolder,
                pdfName,
                pdfBytes,
                "application/pdf",
                cancellationToken);

            var noteName = $"DN-{source.OrderName}";
            await _dataverse.UpdateDeliveryNoteDocumentAsync(deliveryNoteId, noteName, pdfUrl, cancellationToken);

            return new DeliveryNoteDocumentGenerationResult(
                Status: "complete",
                DeliveryNoteId: deliveryNoteId,
                Url: pdfUrl,
                Name: noteName);
        }
        finally
        {
            try
            {
                await _sharePoint.DeleteFileByPathAsync(docxPath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary Word file {DocxPath}", docxPath);
            }
        }
    }

    internal static void EnsureLinesReady(DeliveryNoteDocumentSource source)
    {
        foreach (var line in source.Lines)
        {
            if (line.Quantity is null || line.PriceUnit is null || line.Value is null)
                throw new OrderItemsNotReadyException(source.DeliveryNoteId);
        }
    }

    internal static string BuildFileStem(DeliveryNoteDocumentSource source)
    {
        var account = SanitizeFileSegment(source.Customer.AccountNumber ?? "unknown");
        var order = SanitizeFileSegment(source.OrderName);
        // Keep note created-on so force regenerate overwrites the same path (ops sort by account/order).
        var createdOn = SanitizeFileSegment(source.CreatedOn.UtcDateTime.ToString("yyyy-MM-ddTHH-mm-ssZ"));
        return $"DN-{account}-{order}-{createdOn}";
    }

    private static string SanitizeFileSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['#', '%', '*', ':', '<', '>', '?', '/', '\\', '|', '"'])
            .Distinct()
            .ToArray();

        foreach (var c in invalid)
            value = value.Replace(c, '_');

        return value.Trim();
    }
}
