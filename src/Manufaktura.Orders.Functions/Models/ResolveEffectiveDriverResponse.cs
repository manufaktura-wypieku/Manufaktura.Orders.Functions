namespace Manufaktura.Orders.Functions.Models;

public record ResolveEffectiveDriverResponse(Guid OrderId, Guid? DriverId, string Source, bool IsUncovered);
