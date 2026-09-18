using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Services;

public class DeliveryPackRegenerator : IDeliveryPackRegenerator
{
    private readonly IDataverseService _dataverse;
    private readonly IDocumentMergeService _mergeService;
    private readonly ISharePointService _sharePoint;
    private readonly ILogger<DeliveryPackRegenerator> _logger;

    public DeliveryPackRegenerator(
        IDataverseService dataverse,
        IDocumentMergeService mergeService,
        ISharePointService sharePoint,
        ILogger<DeliveryPackRegenerator> logger)
    {
        _dataverse = dataverse;
        _mergeService = mergeService;
        _sharePoint = sharePoint;
        _logger = logger;
    }

    public async Task<DeliveryPackRegenerationResult> RegenerateAsync(
        Guid? previousDriverId,
        Guid? newDriverId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken = default)
    {
        var drivers = new List<Guid>();
        if (previousDriverId is Guid previous && previous != newDriverId)
            drivers.Add(previous);
        if (newDriverId is Guid next && next != previousDriverId)
            drivers.Add(next);

        if (drivers.Count == 0)
            return DeliveryPackRegenerationResult.None;

        var date = new DateTimeOffset(deliveryDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var warnings = new List<string>();
        var regenerated = 0;
        var locked = 0;

        foreach (var driverId in drivers.Distinct())
        {
            var outcome = await RegenerateGroupingAsync(driverId, date, warnings, cancellationToken);
            regenerated += outcome.Regenerated;
            locked += outcome.Locked;
        }

        return new DeliveryPackRegenerationResult(regenerated, locked, warnings);
    }

    private async Task<(int Regenerated, int Locked)> RegenerateGroupingAsync(
        Guid driverId,
        DateTimeOffset deliveryDate,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var totalNoteCount = await _dataverse.CountTotalDeliveryNotesForDriverAndDateAsync(driverId, deliveryDate, cancellationToken);
        if (totalNoteCount == 0)
            return (await DeleteEmptyActivePackAsync(driverId, deliveryDate, warnings, cancellationToken), 0);

        var notesWithUrlCount = await _dataverse.CountDeliveryNotesWithUrlForDriverAndDateAsync(driverId, deliveryDate, cancellationToken);
        if (notesWithUrlCount < totalNoteCount)
        {
            warnings.Add($"Delivery pack for driver {driverId:D} on {deliveryDate:yyyy-MM-dd} was not rebuilt because not every delivery note has a document.");
            return (0, 0);
        }

        var existingPack = await _dataverse.GetActiveDeliveryPackAsync(driverId, deliveryDate, cancellationToken);
        if (existingPack is not null && existingPack.StatusCode == DeliveryPackStatus.Generating)
        {
            warnings.Add($"Delivery pack {existingPack.Id:D} is already generating.");
            return (0, 0);
        }

        var documentUrls = await _dataverse.GetDeliveryNoteUrlsForDriverAndDateAsync(driverId, deliveryDate, cancellationToken);
        if (documentUrls.Length == 0)
        {
            warnings.Add($"Delivery pack for driver {driverId:D} on {deliveryDate:yyyy-MM-dd} was not rebuilt because no delivery note documents were found.");
            return (0, 0);
        }

        var driverName = await _dataverse.GetDriverNameAsync(driverId, cancellationToken);
        if (string.IsNullOrWhiteSpace(driverName))
            driverName = driverId.ToString("D");
        var packName = $"{driverName} - {deliveryDate:yyyy-MM-dd}";

        Guid packId;
        var createdNew = false;
        if (existingPack is not null)
        {
            packId = existingPack.Id;
            await _dataverse.SetDeliveryPackGeneratingAsync(packId, documentUrls.Length, packName, cancellationToken);
        }
        else
        {
            var (newPackId, created) = await _dataverse.CreateDeliveryPackAsync(driverId, deliveryDate, documentUrls.Length, packName, cancellationToken);
            packId = newPackId;
            createdNew = created;
            if (!created)
            {
                warnings.Add($"Delivery pack {newPackId:D} is already generating.");
                return (0, 0);
            }
        }

        try
        {
            var pdfBytes = await _mergeService.MergeDocumentsAsync(documentUrls, cancellationToken);
            var sharePointUrl = await _sharePoint.UploadDeliveryPackAsync(driverName, deliveryDate, pdfBytes, cancellationToken);
            await _dataverse.UpdateDeliveryPackCompleteAsync(packId, sharePointUrl, documentUrls.Length, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate delivery pack {PackId}", packId);
            await TryMarkFailedAsync(packId, ex, cancellationToken);
            warnings.Add($"Delivery pack {packId:D} could not be rebuilt: {ex.Message}");
            return (0, 0);
        }

        var locked = 0;
        if (createdNew)
            locked = await MarkLockedPacksSupersededAsync(driverId, deliveryDate, packId, warnings, cancellationToken);

        return (1, locked);
    }

    private async Task<int> DeleteEmptyActivePackAsync(
        Guid driverId,
        DateTimeOffset deliveryDate,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var activePack = await _dataverse.GetActiveDeliveryPackAsync(driverId, deliveryDate, cancellationToken);
        if (activePack is null)
            return 0;

        if (activePack.StatusCode == DeliveryPackStatus.Generating)
        {
            warnings.Add($"Empty delivery pack {activePack.Id:D} is already generating and was not deleted.");
            return 0;
        }

        await _dataverse.DeleteDeliveryPackAsync(activePack.Id, cancellationToken);
        return 1;
    }

    private async Task<int> MarkLockedPacksSupersededAsync(
        Guid driverId,
        DateTimeOffset deliveryDate,
        Guid newPackId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var lockedPackIds = await _dataverse.GetLockedDeliveryPackIdsAsync(driverId, deliveryDate, cancellationToken);
        var marked = 0;
        foreach (var lockedPackId in lockedPackIds)
        {
            try
            {
                await _dataverse.AppendDeliveryPackLogAsync(lockedPackId, $"Superseded by delivery pack {newPackId:D}.", cancellationToken);
                marked++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not mark delivery pack {PackId} superseded", lockedPackId);
                warnings.Add($"Locked delivery pack {lockedPackId:D} could not be marked superseded.");
            }
        }

        return marked;
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
