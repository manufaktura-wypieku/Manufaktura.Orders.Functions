using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Configuration;

namespace Manufaktura.Orders.Functions.Services;

public class DataverseService : IDataverseService
{
    // Status codes for mb_deliverypack mb_statusreason field
    internal const int StatusGenerating = 124530001;
    internal const int StatusComplete = 124530002;
    internal const int StatusFailed = 124530003;

    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly string _dataverseUrl;
    private readonly string[] _scopes;

    public DataverseService(HttpClient httpClient, DefaultAzureCredential credential, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _credential = credential;
        _dataverseUrl = configuration["DataverseUrl"]
            ?? throw new InvalidOperationException("DataverseUrl configuration is required.");
        _scopes = [_dataverseUrl.TrimEnd('/') + "/.default"];
    }

    public async Task<DeliveryNoteRecord> GetDeliveryNoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes({id:D})?$select=mb_deliverydate,_mb_deliveryroute_value";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var routeId = Guid.Parse(root.GetProperty("_mb_deliveryroute_value").GetString()!);
        var deliveryDate = root.GetProperty("mb_deliverydate").GetDateTimeOffset();

        return new DeliveryNoteRecord(id, routeId, deliveryDate);
    }

    public async Task<int> CountCompletedOrdersByRouteAndDateAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        // Orders are linked to accounts (via mb_customer). Accounts have mb_deliveryroute.
        // Step 1: find accounts that belong to this route.
        var accountIds = await GetAccountIdsByRouteAsync(routeId, cancellationToken);
        if (accountIds.Length == 0)
            return 0;

        // Step 2: count inactive (statecode = 1) orders for those accounts on the delivery date.
        var dateFrom = deliveryDate.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.Date.AddDays(1).ToString("yyyy-MM-dd");

        var accountFilter = string.Join(" or ", accountIds.Select(id => $"_mb_customerid_value eq {id:D}"));
        var filter = $"statecode eq 1" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z" +
                     $" and ({accountFilter})";

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_orders?$filter={Uri.EscapeDataString(filter)}&$count=true&$select=mb_orderid&$top=1";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("@odata.count", out var countProp) ? countProp.GetInt32() : 0;
    }

    public async Task<int> CountDeliveryNotesWithUrlAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.Date.AddDays(1).ToString("yyyy-MM-dd");

        var filter = $"_mb_deliveryroute_value eq {routeId:D}" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z" +
                     $" and mb_url ne null";

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes?$filter={Uri.EscapeDataString(filter)}&$count=true&$select=mb_deliverynoteid&$top=1";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("@odata.count", out var countProp) ? countProp.GetInt32() : 0;
    }

    public async Task<DeliveryPackRecord?> GetDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.Date.AddDays(1).ToString("yyyy-MM-dd");

        var filter = $"_mb_deliveryroute_value eq {routeId:D}" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z";

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks?$filter={Uri.EscapeDataString(filter)}&$select=mb_deliverypackid,mb_statusreason&$top=1";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var values = doc.RootElement.GetProperty("value");

        if (values.GetArrayLength() == 0)
            return null;

        var first = values[0];
        var packId = Guid.Parse(first.GetProperty("mb_deliverypackid").GetString()!);
        var statusCode = first.GetProperty("mb_statusreason").GetInt32();

        return new DeliveryPackRecord(packId, statusCode);
    }

    public async Task<Guid> CreateDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, int notesCount, CancellationToken cancellationToken = default)
    {
        // OData bind syntax for lookup fields uses a special key name that contains '@'.
        // Anonymous types cannot have such property names, so we use a dictionary.
        var body = new Dictionary<string, object?>
        {
            ["mb_deliverydate"] = deliveryDate.UtcDateTime,
            ["mb_statusreason"] = StatusGenerating,
            ["mb_notescount"] = notesCount,
            ["mb_deliveryroute@odata.bind"] = $"/mb_deliveryroutes({routeId:D})"
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks";
        using var response = await SendAsync(HttpMethod.Post, url, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Created record ID is returned in the OData-EntityId response header.
        if (!response.Headers.TryGetValues("OData-EntityId", out var entityIdHeaderValues))
            throw new InvalidOperationException("Dataverse did not return OData-EntityId after create.");

        var entityIdHeader = entityIdHeaderValues.FirstOrDefault()
            ?? throw new InvalidOperationException("Dataverse did not return OData-EntityId after create.");

        // Header value is a URL like: https://org.crm.dynamics.com/api/data/v9.2/mb_deliverypacks(guid)
        var guidStr = entityIdHeader[(entityIdHeader.LastIndexOf('(') + 1)..entityIdHeader.LastIndexOf(')')];
        return Guid.Parse(guidStr);
    }

    public async Task SetDeliveryPackGeneratingAsync(Guid packId, int notesCount, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_statusreason"] = StatusGenerating,
            ["mb_notescount"] = notesCount
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks({packId:D})";
        using var response = await SendAsync(HttpMethod.Patch, url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string[]> GetDeliveryNoteUrlsAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.Date.AddDays(1).ToString("yyyy-MM-dd");

        var filter = $"_mb_deliveryroute_value eq {routeId:D}" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z" +
                     $" and mb_url ne null";

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes?$filter={Uri.EscapeDataString(filter)}&$select=mb_url";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.GetProperty("mb_url").GetString()!)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToArray();
    }

    public async Task<string?> GetDeliveryRouteNameAsync(Guid routeId, CancellationToken cancellationToken = default)
    {
        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliveryroutes({routeId:D})?$select=mb_name";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("mb_name", out var nameProp) ? nameProp.GetString() : null;
    }

    public async Task UpdateDeliveryPackCompleteAsync(Guid packId, string url, int mergedCount, DateTimeOffset generatedOn, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_statusreason"] = StatusComplete,
            ["mb_url"] = url,
            ["mb_mergedcount"] = mergedCount,
            ["mb_generatedon"] = generatedOn.UtcDateTime
        };

        var requestUrl = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks({packId:D})";
        using var response = await SendAsync(HttpMethod.Patch, requestUrl, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateDeliveryPackFailedAsync(Guid packId, string log, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_statusreason"] = StatusFailed,
            ["mb_log"] = log
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks({packId:D})";
        using var response = await SendAsync(HttpMethod.Patch, url, body, cancellationToken);
        // Best-effort: do not throw if status update also fails.
    }

    private async Task<Guid[]> GetAccountIdsByRouteAsync(Guid routeId, CancellationToken cancellationToken)
    {
        var filter = $"_mb_deliveryroute_value eq {routeId:D}";
        var url = $"{_dataverseUrl}/api/data/v9.2/accounts?$filter={Uri.EscapeDataString(filter)}&$select=accountid";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => Guid.Parse(e.GetProperty("accountid").GetString()!))
            .ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _httpClient.SendAsync(request, cancellationToken);
    }
}
