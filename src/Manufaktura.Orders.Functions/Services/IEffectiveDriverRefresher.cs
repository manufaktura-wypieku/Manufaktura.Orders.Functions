using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IEffectiveDriverRefresher
{
    Task<RefreshEffectiveDriversResponse> RefreshAsync(RefreshEffectiveDriversRequest request, CancellationToken cancellationToken = default);
}
