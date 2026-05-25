using System.Net.Http.Json;
using System.Text.Json;
using Azure.Identity;
using Xunit;

namespace Manufaktura.Orders.Functions.IntegrationTests;

public sealed class DataverseIntegrationFixture : IAsyncLifetime
{
    private readonly HttpClient _dataverseHttp = new();
    private readonly HttpClient _functionHttp = new();
    private readonly List<CreatedRecord> _createdRecords = [];
    private readonly List<Guid> _createdOrderIds = [];

    public DataverseTestClient Dataverse { get; private set; } = null!;
    public string DataverseUrl { get; private set; } = string.Empty;
    public string FunctionAppBaseUrl { get; private set; } = string.Empty;
    public string FunctionAppKey { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        DataverseUrl = GetRequiredEnvironmentVariable("DATAVERSE_URL").TrimEnd('/');

        var credential = new DefaultAzureCredential();
        Dataverse = new DataverseTestClient(_dataverseHttp, credential, DataverseUrl);

        var dataverseFunctionBaseUrl = await GetRequiredDataverseEnvironmentVariableAsync("mb_OrdersFunctionAppBaseUrl", CancellationToken.None);
        var dataverseFunctionKey = await GetRequiredDataverseEnvironmentVariableAsync("mb_OrdersFunctionAppKey", CancellationToken.None);

        FunctionAppBaseUrl = GetOptionalEnvironmentVariable("FUNCTION_APP_BASE_URL") ?? dataverseFunctionBaseUrl;
        FunctionAppKey = GetOptionalEnvironmentVariable("FUNCTION_APP_KEY") ?? dataverseFunctionKey;

        _functionHttp.BaseAddress = new Uri($"{FunctionAppBaseUrl.TrimEnd('/')}/");
        _functionHttp.DefaultRequestHeaders.Add("x-functions-key", FunctionAppKey);
    }

    public async Task<Guid> CreateDriverContactAsync(string suffix, CancellationToken cancellationToken)
    {
        var id = await Dataverse.CreateEntityAsync("contacts", new Dictionary<string, object?>
        {
            ["firstname"] = "Orders",
            ["lastname"] = $"Smoke Driver {suffix}",
            ["emailaddress1"] = $"orders-smoke-{suffix.ToLowerInvariant()}@example.invalid"
        }, cancellationToken);

        Register("contacts", id);
        return id;
    }

    public async Task<Guid> CreateDeliveryRouteAsync(string suffix, Guid driverId, DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_name"] = $"[E2E] Smoke Route {suffix}",
            ["mb_driver@odata.bind"] = $"/contacts({driverId:D})",
            [$"{GetWeekdayDriverNavigationProperty(deliveryDate.DayOfWeek)}@odata.bind"] = $"/contacts({driverId:D})"
        };

        var id = await Dataverse.CreateEntityAsync("mb_deliveryroutes", body, cancellationToken);
        Register("mb_deliveryroutes", id);
        return id;
    }

    public async Task<Guid> CreateAccountAsync(string suffix, Guid deliveryRouteId, Guid? priceListId, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = $"[E2E] Smoke Account {suffix}",
            ["accountnumber"] = $"E2E-{suffix}",
            ["mb_DeliveryRoute@odata.bind"] = $"/mb_deliveryroutes({deliveryRouteId:D})"
        };

        if (priceListId is Guid configuredPriceListId)
            body["mb_Pricelist@odata.bind"] = $"/mb_pricelists({configuredPriceListId:D})";

        var id = await Dataverse.CreateEntityAsync("accounts", body, cancellationToken);
        Register("accounts", id);
        return id;
    }

    public async Task<Guid> CreateOrderAsync(string suffix, Guid accountId, DateOnly deliveryDate, Guid? priceListId, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_name"] = $"[E2E] Smoke Order {suffix}",
            ["mb_deliverydate"] = deliveryDate.ToString("yyyy-MM-dd"),
            ["mb_Customer_account@odata.bind"] = $"/accounts({accountId:D})"
        };

        if (priceListId is Guid configuredPriceListId)
            body["mb_Pricelist@odata.bind"] = $"/mb_pricelists({configuredPriceListId:D})";

        var id = await Dataverse.CreateEntityAsync("mb_orders", body, cancellationToken);
        Register("mb_orders", id);
        _createdOrderIds.Add(id);
        return id;
    }

    public async Task<JsonDocument> RefreshEffectiveDriversAsync(Guid orderId, CancellationToken cancellationToken)
    {
        using var response = await _functionHttp.PostAsJsonAsync("api/RefreshEffectiveDrivers", new { orderId }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"RefreshEffectiveDrivers returned {(int)response.StatusCode} {response.ReasonPhrase}. {body}",
                null,
                response.StatusCode);
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    public async Task<OrderDriverSnapshot> WaitForOrderDriverSnapshotAsync(
        Guid orderId,
        Guid expectedRouteId,
        Guid expectedDriverId,
        string expectedSource,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        OrderDriverSnapshot? lastSnapshot = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastSnapshot = await Dataverse.GetOrderDriverSnapshotAsync(orderId, cancellationToken);
            if (lastSnapshot.HomeDeliveryRouteId == expectedRouteId &&
                lastSnapshot.EffectiveDriverId == expectedDriverId &&
                string.Equals(lastSnapshot.EffectiveDriverSource, expectedSource, StringComparison.Ordinal))
            {
                return lastSnapshot;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException(
            $"Order {orderId:D} did not receive expected driver fields within {timeout}. " +
            $"Last snapshot: route={lastSnapshot?.HomeDeliveryRouteId?.ToString("D") ?? "<null>"}, " +
            $"driver={lastSnapshot?.EffectiveDriverId?.ToString("D") ?? "<null>"}, " +
            $"source={lastSnapshot?.EffectiveDriverSource ?? "<null>"}.");
    }

    public static DateOnly GetNextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
            daysUntilMonday = 7;

        return today.AddDays(daysUntilMonday);
    }

    public static string CreateRunSuffix()
        => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public async ValueTask DisposeAsync()
    {
        foreach (var orderId in _createdOrderIds.Distinct())
            await Dataverse.DeleteOrderItemsForOrderAsync(orderId, CancellationToken.None);

        foreach (var record in _createdRecords.AsEnumerable().Reverse())
        {
            if (record.EntitySetName == "mb_orders")
                await Dataverse.DeleteOrderItemsForOrderAsync(record.Id, CancellationToken.None);

            await Dataverse.DeleteEntityAsync(record.EntitySetName, record.Id, CancellationToken.None);
        }

        _functionHttp.Dispose();
        _dataverseHttp.Dispose();
    }

    private async Task<string> GetRequiredDataverseEnvironmentVariableAsync(string schemaName, CancellationToken cancellationToken)
    {
        var value = await Dataverse.GetEnvironmentVariableValueAsync(schemaName, cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Dataverse environment variable {schemaName} is missing or blank. The Orders flow glue and integration tests require it.");

        return value;
    }

    private void Register(string entitySetName, Guid id) => _createdRecords.Add(new CreatedRecord(entitySetName, id));

    private static string GetRequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} environment variable is required for Orders integration tests.");

    private static string? GetOptionalEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string GetWeekdayDriverNavigationProperty(DayOfWeek dayOfWeek)
        => dayOfWeek switch
        {
            DayOfWeek.Monday => "mb_drivermonday",
            DayOfWeek.Tuesday => "mb_drivertuesday",
            DayOfWeek.Wednesday => "mb_driverwednesday",
            DayOfWeek.Thursday => "mb_driverthursday",
            DayOfWeek.Friday => "mb_driverfriday",
            DayOfWeek.Saturday => "mb_driversaturday",
            DayOfWeek.Sunday => "mb_driversunday",
            _ => throw new ArgumentOutOfRangeException(nameof(dayOfWeek), dayOfWeek, null)
        };

    private sealed record CreatedRecord(string EntitySetName, Guid Id);
}