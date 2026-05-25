using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IDataverseService
{
    /// <summary>
    /// Loads the order, route rota, account overrides, and driver absences needed to resolve an order's effective driver.
    /// </summary>
    Task<EffectiveDriverResolutionRequest> GetEffectiveDriverResolutionRequestForOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds current or future orders whose effective driver should be recalculated.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetOrderIdsForEffectiveDriverRefreshAsync(EffectiveDriverRefreshQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the home delivery route, effective driver, and source on an order.
    /// </summary>
    Task UpdateOrderEffectiveDriverAsync(Guid orderId, Guid homeDeliveryRouteId, EffectiveDriverResolutionResult resolution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a delivery note by its ID, returning the linked order's effective driver and delivery date.
    /// </summary>
    Task<DeliveryNoteRecord> GetDeliveryNoteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts all delivery notes whose linked orders are assigned to the given effective driver and delivery date.
    /// </summary>
    Task<int> CountTotalDeliveryNotesForDriverAndDateAsync(Guid effectiveDriverId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts delivery notes with SharePoint URLs whose linked orders are assigned to the given effective driver and delivery date.
    /// </summary>
    Task<int> CountDeliveryNotesWithUrlForDriverAndDateAsync(Guid effectiveDriverId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active delivery pack record for the given effective driver and delivery date, or null if none exists.
    /// </summary>
    Task<DeliveryPackRecord?> GetActiveDeliveryPackAsync(Guid effectiveDriverId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new delivery pack record in Generating status.
    /// Returns <c>created = false</c> when Dataverse returned 409 Conflict (concurrent creation).
    /// </summary>
    /// <param name="effectiveDriverId">The effective driver identifier for the pack.</param>
    /// <param name="deliveryDate">The delivery date the pack is created for.</param>
    /// <param name="notesCount">The expected number of delivery notes in the pack.</param>
    /// <param name="packName">The display name to store in Dataverse <c>mb_name</c>. Null, empty, or whitespace values are ignored.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task<(Guid packId, bool created)> CreateDeliveryPackAsync(Guid effectiveDriverId, DateTimeOffset deliveryDate, int notesCount, string? packName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an existing delivery pack to Generating status, updates the expected notes count,
    /// and updates <c>mb_name</c> when a non-null, non-whitespace pack name is provided.
    /// </summary>
    /// <param name="packId">The ID of the existing delivery pack to update.</param>
    /// <param name="notesCount">The expected number of notes in the delivery pack.</param>
    /// <param name="packName">The delivery pack name to store in <c>mb_name</c>. If null, empty, or whitespace, the existing name is left unchanged.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task SetDeliveryPackGeneratingAsync(Guid packId, int notesCount, string? packName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the SharePoint URLs of all delivery notes whose linked orders are assigned to the given effective driver and delivery date.
    /// </summary>
    Task<string[]> GetDeliveryNoteUrlsForDriverAndDateAsync(Guid effectiveDriverId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the display name of a contact used as an effective driver.
    /// </summary>
    Task<string?> GetDriverNameAsync(Guid driverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the delivery pack to Complete status with the merged PDF URL, merged count, and generated-on timestamp.
    /// </summary>
    Task UpdateDeliveryPackCompleteAsync(Guid packId, string url, int mergedCount, DateTimeOffset generatedOn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the delivery pack to Failed status and records the error log.
    /// </summary>
    Task UpdateDeliveryPackFailedAsync(Guid packId, string log, CancellationToken cancellationToken = default);
}
