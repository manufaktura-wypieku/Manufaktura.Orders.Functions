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
    /// Counts all delivery notes for the given route and delivery date, regardless of whether mb_url is populated.
    /// </summary>
    Task<int> CountTotalDeliveryNotesAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default);

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
    /// <param name="routeId">The delivery route identifier for the pack.</param>
    /// <param name="deliveryDate">The delivery date the pack is created for.</param>
    /// <param name="notesCount">The expected number of delivery notes in the pack.</param>
    /// <param name="packName">The display name to store in Dataverse <c>mb_name</c>. Null, empty, or whitespace values are ignored.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task<(Guid packId, bool created)> CreateDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, int notesCount, string? packName, CancellationToken cancellationToken = default);

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
