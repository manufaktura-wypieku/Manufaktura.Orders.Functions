using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IDeliveryNoteDocumentGenerator
{
    Task<DeliveryNoteDocumentGenerationResult> GenerateAsync(Guid deliveryNoteId, bool force, CancellationToken cancellationToken = default);
}

public sealed record DeliveryNoteDocumentGenerationResult(
    string Status,
    string? Reason = null,
    Guid? DeliveryNoteId = null,
    string? Url = null,
    string? Name = null);
