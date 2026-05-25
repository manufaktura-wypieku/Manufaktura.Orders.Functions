using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Identity;
using Xunit;

namespace Manufaktura.Orders.Functions.IntegrationTests;

public sealed class DataverseIntegrationFixture : IAsyncLifetime
{
    private const string LocalSettingsFileName = "integration-tests.local.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LocalSettings = new(LoadLocalSettings);

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
        DataverseUrl = GetRequiredConfigurationValue("DATAVERSE_URL").TrimEnd('/');

        var credential = new DefaultAzureCredential();
        Dataverse = new DataverseTestClient(_dataverseHttp, credential, DataverseUrl);

        var dataverseFunctionBaseUrl = await GetRequiredDataverseEnvironmentVariableAsync("mb_OrdersFunctionAppBaseUrl", CancellationToken.None);
        var dataverseFunctionKey = await GetRequiredDataverseEnvironmentVariableAsync("mb_OrdersFunctionAppKey", CancellationToken.None);

        FunctionAppBaseUrl = GetOptionalConfigurationValue("FUNCTION_APP_BASE_URL") ?? dataverseFunctionBaseUrl;
        FunctionAppKey = GetOptionalConfigurationValue("FUNCTION_APP_KEY") ?? dataverseFunctionKey;

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
        => await PostFunctionJsonAsync("api/RefreshEffectiveDrivers", new { orderId }, HttpStatusCode.OK, cancellationToken);

    public async Task<JsonDocument> ResolveEffectiveDriverAsync(Guid orderId, CancellationToken cancellationToken)
        => await PostFunctionJsonAsync("api/ResolveEffectiveDriver", new { orderId }, HttpStatusCode.OK, cancellationToken);

    public async Task<JsonDocument> PostFunctionJsonAsync(
        string relativePath,
        object body,
        HttpStatusCode expectedStatusCode,
        CancellationToken cancellationToken)
    {
        using var response = await _functionHttp.PostAsJsonAsync(relativePath, body, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != expectedStatusCode)
        {
            throw new HttpRequestException(
                $"{relativePath} returned {(int)response.StatusCode} {response.ReasonPhrase}; expected {(int)expectedStatusCode} {expectedStatusCode}. {responseBody}",
                null,
                response.StatusCode);
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody);
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

    private static string GetRequiredConfigurationValue(string name)
        => GetOptionalConfigurationValue(name)
            ?? throw new InvalidOperationException(
                $"{name} is required for Orders integration tests. Set it as an environment variable in the same process that launches dotnet test, " +
                $"or create {LocalSettingsFileName} in the integration test project folder. Environment variables set with $env: only apply to that PowerShell terminal and are not inherited by VS Code Test Explorer.");

    private static string? GetOptionalConfigurationValue(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } environmentValue)
            return environmentValue;

        if (AppContext.GetData(name) is string appContextValue && !string.IsNullOrWhiteSpace(appContextValue))
            return appContextValue;

        return LocalSettings.Value.TryGetValue(name, out var localValue) && !string.IsNullOrWhiteSpace(localValue)
            ? localValue
            : null;
    }

    private static IReadOnlyDictionary<string, string> LoadLocalSettings()
    {
        var path = FindLocalSettingsPath();
        if (path is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                settings[property.Name] = property.Value.GetString()!;
            }
        }

        return settings;
    }

    private static string? FindLocalSettingsPath()
    {
        var searchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null && searchedDirectories.Add(directory.FullName))
            {
                var candidate = Path.Combine(directory.FullName, LocalSettingsFileName);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }
        }

        return null;
    }

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