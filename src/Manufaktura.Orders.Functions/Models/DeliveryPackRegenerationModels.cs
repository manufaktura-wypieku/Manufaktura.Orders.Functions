namespace Manufaktura.Orders.Functions.Models;

public record OrderDeliverySnapshot(Guid? EffectiveDriverId, DateOnly DeliveryDate, bool HasDeliveryNotes);

public record DeliveryPackRegenerationResult(int RegeneratedPacks, int LockedPacksRequiringReview, IReadOnlyList<string> Warnings)
{
    public static DeliveryPackRegenerationResult None { get; } = new(0, 0, []);
}
