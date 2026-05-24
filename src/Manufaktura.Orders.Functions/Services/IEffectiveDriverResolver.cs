using Manufaktura.Orders.Functions.Models;

namespace Manufaktura.Orders.Functions.Services;

public interface IEffectiveDriverResolver
{
    EffectiveDriverResolutionResult Resolve(EffectiveDriverResolutionRequest request);
}
