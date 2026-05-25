using Xunit;

namespace Manufaktura.Orders.Functions.IntegrationTests;

public sealed class OrdersFlowEndToEndTests(DataverseIntegrationFixture fixture) : IClassFixture<DataverseIntegrationFixture>
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task OrderCreated_Flow_PopulatesOrderDriverFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = DataverseIntegrationFixture.CreateRunSuffix();
        var deliveryDate = DataverseIntegrationFixture.GetNextMonday();

        var priceListId = await fixture.Dataverse.GetFirstReusablePriceListIdAsync(cancellationToken);
        var driverId = await fixture.CreateDriverContactAsync(suffix, cancellationToken);
        var routeId = await fixture.CreateDeliveryRouteAsync(suffix, driverId, deliveryDate, cancellationToken);
        var accountId = await fixture.CreateAccountAsync(suffix, routeId, priceListId, cancellationToken);
        var orderId = await fixture.CreateOrderAsync(suffix, accountId, deliveryDate, priceListId, cancellationToken);

        var snapshot = await fixture.WaitForOrderDriverSnapshotAsync(
            orderId,
            routeId,
            driverId,
            "RouteWeekday",
            TimeSpan.FromSeconds(GetTimeoutSeconds("E2E_FLOW_TIMEOUT_SECONDS", 240)),
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
