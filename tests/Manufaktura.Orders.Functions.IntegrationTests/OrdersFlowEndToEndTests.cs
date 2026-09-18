using Xunit;

namespace Manufaktura.Orders.Functions.IntegrationTests;

public sealed class OrdersFlowEndToEndTests(DataverseIntegrationFixture fixture) : IClassFixture<DataverseIntegrationFixture>
{
    private const int DefaultFlowTimeoutSeconds = 120;
    private const int DefaultPackTimeoutSeconds = 60;
    private const int DeliveryPackCompleteStatusReason = 124530002;

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
            TimeSpan.FromSeconds(GetTimeoutSeconds("E2E_FLOW_TIMEOUT_SECONDS", DefaultFlowTimeoutSeconds)),
            cancellationToken);

        Assert.Equal(routeId, snapshot.HomeDeliveryRouteId);
        Assert.Equal(driverId, snapshot.EffectiveDriverId);
        Assert.Equal("RouteWeekday", snapshot.EffectiveDriverSource);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task OrderDeactivated_Flows_CreateDeliveryPackForEffectiveDriver()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = DataverseIntegrationFixture.CreateRunSuffix();
        var deliveryDate = DataverseIntegrationFixture.GetNextMonday();
        var flowTimeout = TimeSpan.FromSeconds(GetTimeoutSeconds("E2E_FLOW_TIMEOUT_SECONDS", DefaultFlowTimeoutSeconds));
        var packTimeout = TimeSpan.FromSeconds(GetTimeoutSeconds("E2E_PACK_TIMEOUT_SECONDS", DefaultPackTimeoutSeconds));

        var priceListId = await fixture.Dataverse.GetFirstReusablePriceListIdAsync(cancellationToken);
        var driverId = await fixture.CreateDriverContactAsync(suffix, cancellationToken);
        var routeId = await fixture.CreateDeliveryRouteAsync(suffix, driverId, deliveryDate, cancellationToken);
        var accountId = await fixture.CreateAccountAsync(suffix, routeId, priceListId, cancellationToken);
        var orderId = await fixture.CreateOrderAsync(suffix, accountId, deliveryDate, priceListId, cancellationToken);

        using var refreshResult = await fixture.RefreshEffectiveDriversAsync(orderId, cancellationToken);
        Assert.Equal("updated", refreshResult.RootElement.GetProperty("results")[0].GetProperty("status").GetString());

        await fixture.WaitForOrderDriverSnapshotAsync(
            orderId,
            routeId,
            driverId,
            "RouteWeekday",
            flowTimeout,
            cancellationToken);

        await fixture.WaitForGeneratedOrderItemsReadyAsync(orderId, flowTimeout, cancellationToken);

        await fixture.DeactivateOrderAsync(orderId, cancellationToken);
        var note = await fixture.WaitForDeliveryNoteUrlForOrderAsync(orderId, flowTimeout, cancellationToken);

        var pack = await fixture.WaitForDeliveryPackAsync(
            driverId,
            deliveryDate,
            DeliveryPackCompleteStatusReason,
            packTimeout,
            cancellationToken);

        Assert.Equal(driverId, pack.EffectiveDriverId);
        Assert.Equal(deliveryDate, pack.DeliveryDate);
        Assert.Equal(1, pack.NotesCount);
        Assert.False(string.IsNullOrWhiteSpace(note.Url));
        Assert.False(string.IsNullOrWhiteSpace(pack.Url));
    }

    private static int GetTimeoutSeconds(string environmentVariableName, int defaultValue)
        => int.TryParse(Environment.GetEnvironmentVariable(environmentVariableName), out var configuredValue) && configuredValue > 0
            ? configuredValue
            : defaultValue;
}
