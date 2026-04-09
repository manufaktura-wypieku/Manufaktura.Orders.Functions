namespace Manufaktura.Orders.Functions.Models;

public class MissingDeliveryRouteException : Exception
{
    public Guid DeliveryNoteId { get; }

    public MissingDeliveryRouteException(Guid deliveryNoteId)
        : base($"Delivery note '{deliveryNoteId:D}' has no delivery route assigned (_mb_deliveryroute_value is null or empty).")
    {
        DeliveryNoteId = deliveryNoteId;
    }
}
