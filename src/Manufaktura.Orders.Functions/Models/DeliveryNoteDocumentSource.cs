namespace Manufaktura.Orders.Functions.Models;

public sealed record DeliveryNoteDocumentSource(
    Guid DeliveryNoteId,
    DateTimeOffset CreatedOn,
    string? ExistingUrl,
    string OrderName,
    DateTimeOffset? DeliveryDate,
    decimal? OrderTotal,
    DeliveryNoteCustomer Customer,
    IReadOnlyList<DeliveryNoteLine> Lines);

public sealed record DeliveryNoteCustomer(
    string? Name,
    string? AccountNumber,
    string? AddressLine1,
    string? PostalCode,
    string? City,
    bool RedBasket);

public sealed record DeliveryNoteLine(
    string? NamePl,
    string? NameEn,
    decimal? Quantity,
    decimal? PriceUnit,
    decimal? Value,
    string? ProductNumber);
