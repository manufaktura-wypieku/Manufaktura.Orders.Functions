namespace Manufaktura.Orders.Functions.Models;

public record DeliveryNoteRecord(Guid Id, Guid OrderId, Guid EffectiveDriverId, DateTimeOffset DeliveryDate);
