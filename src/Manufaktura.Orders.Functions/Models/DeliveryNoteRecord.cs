namespace Manufaktura.Orders.Functions.Models;

public record DeliveryNoteRecord(Guid Id, Guid RouteId, DateTimeOffset DeliveryDate);
