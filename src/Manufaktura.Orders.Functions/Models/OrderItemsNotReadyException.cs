namespace Manufaktura.Orders.Functions.Models;

public sealed class OrderItemsNotReadyException : Exception
{
    public Guid DeliveryNoteId { get; }

    public OrderItemsNotReadyException(Guid deliveryNoteId)
        : base($"Delivery note '{deliveryNoteId:D}' has order lines that are missing quantity, unit price, or value.")
    {
        DeliveryNoteId = deliveryNoteId;
    }
}
