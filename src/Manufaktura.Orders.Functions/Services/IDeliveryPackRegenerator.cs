using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IDeliveryPackRegenerator
{
    Task<DeliveryPackRegenerationResult> RegenerateAsync(
        Guid? previousDriverId,
        Guid? newDriverId,
        DateOnly deliveryDate,
        CancellationToken cancellationToken = default);
}
