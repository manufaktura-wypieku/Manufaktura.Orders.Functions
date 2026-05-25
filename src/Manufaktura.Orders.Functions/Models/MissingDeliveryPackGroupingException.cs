namespace Manufaktura.Orders.Functions.Models;

public class MissingDeliveryPackGroupingException : Exception
{
    public Guid DeliveryNoteId { get; }

    public string Code { get; }

    public MissingDeliveryPackGroupingException(Guid deliveryNoteId, string code, string message)
        : base(message)
    {
        DeliveryNoteId = deliveryNoteId;
        Code = code;
    }

    public static MissingDeliveryPackGroupingException MissingOrder(Guid deliveryNoteId) =>
        new(deliveryNoteId, "missing_order", $"Delivery note '{deliveryNoteId:D}' has no order assigned.");

    public static MissingDeliveryPackGroupingException MissingDeliveryDate(Guid deliveryNoteId, Guid orderId) =>
        new(deliveryNoteId, "missing_delivery_date", $"Order '{orderId:D}' linked to delivery note '{deliveryNoteId:D}' has no delivery date assigned.");

    public static MissingDeliveryPackGroupingException MissingEffectiveDriver(Guid deliveryNoteId, Guid orderId) =>
        new(deliveryNoteId, "missing_effective_driver", $"Order '{orderId:D}' linked to delivery note '{deliveryNoteId:D}' has no effective driver assigned.");
}