using Xunit;

namespace Manufaktura.Orders.Functions.IntegrationTests;

public sealed class OrdersSmokeIntegrationTests(DataverseIntegrationFixture fixture) : IClassFixture<DataverseIntegrationFixture>
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RefreshEffectiveDrivers_DirectFunction_PopulatesOrderDriverFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = DataverseIntegrationFixture.CreateRunSuffix();
        var deliveryDate = DataverseIntegrationFixture.GetNextMonday();

        var priceListId = await fixture.Dataverse.GetFirstReusablePriceListIdAsync(cancellationToken);
        var driverId = await fixture.CreateDriverContactAsync(suffix, cancellationToken);
        var routeId = await fixture.CreateDeliveryRouteAsync(suffix, driverId, deliveryDate, cancellationToken);
        var accountId = await fixture.CreateAccountAsync(suffix, routeId, priceListId, cancellationToken);
        var orderId = await fixture.CreateOrderAsync(suffix, accountId, deliveryDate, priceListId, cancellationToken);

        using var refreshResult = await fixture.RefreshEffectiveDriversAsync(orderId, cancellationToken);
        Assert.Equal("updated", refreshResult.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        var snapshot = await fixture.WaitForOrderDriverSnapshotAsync(
            orderId,
            routeId,
            driverId,
            "RouteWeekday",
            TimeSpan.FromSeconds(GetTimeoutSeconds("E2E_FAST_TIMEOUT_SECONDS", 30)),
            cancellationToken);

        Assert.Equal(routeId, snapshot.HomeDeliveryRouteId);
        Assert.Equal(driverId, snapshot.EffectiveDriverId);
        Assert.Equal("RouteWeekday", snapshot.EffectiveDriverSource);
    }

    private static int GetTimeoutSeconds(string environmentVariableName, int defaultValue)
        => int.TryParse(Environment.GetEnvironmentVariable(environmentVariableName), out var configuredValue) && configuredValue > 0
            ? configuredValue
            : defaultValue;
}