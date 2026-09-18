namespace Manufaktura.Orders.Functions.Models;

public sealed record EmptyOrderAccount(Guid AccountId, string? Name, Guid? PriceListId);
