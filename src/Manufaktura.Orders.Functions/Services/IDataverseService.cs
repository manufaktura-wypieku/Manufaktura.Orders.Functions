using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IDataverseService
{
    /// <summary>
    /// Retrieves a delivery note by its ID, returning the route and delivery date.
    /// </summary>
    Task<DeliveryNoteRecord> GetDeliveryNoteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts inactive (completed) orders whose customer account belongs to the given route, for the given delivery date.
    /// </summary>
    Task<int> CountCompletedOrdersByRouteAndDateAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts delivery notes for the given route and delivery date that have a SharePoint URL (mb_url) populated.
    /// </summary>
    Task<int> CountDeliveryNotesWithUrlAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an existing delivery pack record for the given route and delivery date, or null if none exists.
    /// </summary>
    Task<DeliveryPackRecord?> GetDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new delivery pack record in Generating status.
    /// Returns <c>created = false</c> when Dataverse returned 409 Conflict (concurrent creation).
    /// </summary>
    Task<(Guid packId, bool created)> CreateDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, int notesCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an existing delivery pack to Generating status and updates the expected notes count.
    /// </summary>
    Task SetDeliveryPackGeneratingAsync(Guid packId, int notesCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the SharePoint URLs of all delivery notes for the given route and delivery date that have mb_url populated.
    /// </summary>
    Task<string[]> GetDeliveryNoteUrlsAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the display name (mb_name) of a delivery route record.
    /// </summary>
    Task<string?> GetDeliveryRouteNameAsync(Guid routeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the delivery pack to Complete status with the merged PDF URL, merged count, and generated-on timestamp.
    /// </summary>
    Task UpdateDeliveryPackCompleteAsync(Guid packId, string url, int mergedCount, DateTimeOffset generatedOn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the delivery pack to Failed status and records the error log.
    /// </summary>
    Task UpdateDeliveryPackFailedAsync(Guid packId, string log, CancellationToken cancellationToken = default);
}
