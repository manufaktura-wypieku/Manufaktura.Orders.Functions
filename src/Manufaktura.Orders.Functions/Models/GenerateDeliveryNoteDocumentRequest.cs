namespace Manufaktura.Orders.Functions.Models;

public class GenerateDeliveryNoteDocumentRequest
{
    public Guid DeliveryNoteId { get; init; }

    /// <summary>
    /// When true, regenerates even if <c>mb_url</c> is already set.
    /// </summary>
    public bool Force { get; init; }
}
