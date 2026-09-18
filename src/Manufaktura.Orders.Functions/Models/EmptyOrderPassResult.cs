namespace Manufaktura.Orders.Functions.Models;

public sealed record EmptyOrderPassResult(
    DateOnly DeliveryDate,
    int Created,
    int AlreadyPresent,
    int MissingPriceList,
    int Failed);
