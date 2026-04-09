using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Configuration;

namespace Manufaktura.Orders.Functions.Services;

public class DataverseService : IDataverseService
{
    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly string _dataverseUrl;
    private readonly string[] _scopes;

    public DataverseService(HttpClient httpClient, TokenCredential credential, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _credential = credential;
        _dataverseUrl = (configuration["DataverseUrl"]
            ?? throw new InvalidOperationException("DataverseUrl configuration is required."))
            .TrimEnd('/');
        _scopes = [_dataverseUrl + "/.default"];
    }

    public async Task<DeliveryNoteRecord> GetDeliveryNoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes({id:D})?$select=mb_deliverydate,_mb_deliveryroute_value";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var routeIdStr = root.GetProperty("_mb_deliveryroute_value").GetString();
        if (string.IsNullOrEmpty(routeIdStr))
            throw new MissingDeliveryRouteException(id);
        var routeId = Guid.Parse(routeIdStr);
        var deliveryDate = root.GetProperty("mb_deliverydate").GetDateTimeOffset();

        return new DeliveryNoteRecord(id, routeId, deliveryDate);
    }

    public async Task<int> CountCompletedOrdersByRouteAndDateAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        // Use FetchXML with a link-entity join to filter orders by the account's delivery route,
        // avoiding URL-length issues from expanding all account IDs into an OData OR filter.
        var utcDeliveryDate = deliveryDate.UtcDateTime.Date;
        var dateFrom = utcDeliveryDate.ToString("yyyy-MM-dd");
        var dateTo = utcDeliveryDate.AddDays(1).ToString("yyyy-MM-dd");

        var fetchXml =
            $"""
            <fetch aggregate='true'>
              <entity name='mb_order'>
                <attribute name='mb_orderid' alias='ordercount' aggregate='count' />
                <filter type='and'>
                  <condition attribute='statecode' operator='eq' value='1' />
                  <condition attribute='mb_deliverydate' operator='on-or-after' value='{dateFrom}' />
                  <condition attribute='mb_deliverydate' operator='before' value='{dateTo}' />
                </filter>
                <link-entity name='account' from='accountid' to='mb_customerid' link-type='inner'>
                  <filter type='and'>
                    <condition attribute='mb_deliveryroute' operator='eq' value='{routeId:D}' />
                  </filter>
                </link-entity>
              </entity>
            </fetch>
            """;

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_orders?fetchXml={Uri.EscapeDataString(fetchXml)}";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("value", out var valueProp) || valueProp.GetArrayLength() == 0)
            return 0;

        var firstRow = valueProp[0];
        if (!firstRow.TryGetProperty("ordercount", out var countProp))
            return 0;

        return countProp.ValueKind switch
        {
            JsonValueKind.Number => countProp.GetInt32(),
            JsonValueKind.String when int.TryParse(countProp.GetString(), out var count) => count,
            _ => 0
        };
    }

    public async Task<int> CountDeliveryNotesWithUrlAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.UtcDateTime.Date.AddDays(1).ToString("yyyy-MM-dd");

        var filter = $"_mb_deliveryroute_value eq {routeId:D}" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z" +
                     $" and mb_url ne null and mb_url ne ''";

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes?$filter={Uri.EscapeDataString(filter)}&$count=true&$select=mb_deliverynoteid&$top=1";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("@odata.count", out var countProp) ? countProp.GetInt32() : 0;
    }

    public async Task<DeliveryPackRecord?> GetDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.UtcDateTime.Date.AddDays(1).ToString("yyyy-MM-dd");

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

    public async Task<(Guid packId, bool created)> CreateDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, int notesCount, CancellationToken cancellationToken = default)
    {
        // OData bind syntax for lookup fields uses a special key name that contains '@'.
        // Anonymous types cannot have such property names, so we use a dictionary.
        var body = new Dictionary<string, object?>
        {
            ["mb_deliverydate"] = deliveryDate.UtcDateTime,
            ["mb_statusreason"] = DeliveryPackStatus.Generating,
            ["mb_notescount"] = notesCount,
            ["mb_deliveryroute@odata.bind"] = $"/mb_deliveryroutes({routeId:D})"
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks";
        using var response = await SendAsync(HttpMethod.Post, url, body, cancellationToken);

        // 409 Conflict: a concurrent request already created the pack; re-query and return its ID.
        // NOTE: this guard only prevents duplicates when Dataverse enforces a uniqueness alternate key
        // for (mb_deliveryroute, mb_deliverydate). Ensure that alternate key is configured in the
        // solution before deploying to production.
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var existing = await GetDeliveryPackAsync(routeId, deliveryDate, cancellationToken)
                ?? throw new InvalidOperationException("Dataverse returned 409 Conflict but no delivery pack was found for the route/date.");
            return (existing.Id, false);
        }

        response.EnsureSuccessStatusCode();

        // Created record ID is returned in the OData-EntityId response header.
        if (!response.Headers.TryGetValues("OData-EntityId", out var entityIdHeaderValues))
            throw new InvalidOperationException("Dataverse did not return OData-EntityId after create.");

        var entityIdHeader = entityIdHeaderValues.FirstOrDefault()
            ?? throw new InvalidOperationException("Dataverse did not return OData-EntityId after create.");

        // Header value is a URL like: https://org.crm.dynamics.com/api/data/v9.2/mb_deliverypacks(guid)
        var guidStr = entityIdHeader[(entityIdHeader.LastIndexOf('(') + 1)..entityIdHeader.LastIndexOf(')')];
        return (Guid.Parse(guidStr), true);
    }

    public async Task SetDeliveryPackGeneratingAsync(Guid packId, int notesCount, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_statusreason"] = DeliveryPackStatus.Generating,
            ["mb_notescount"] = notesCount
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks({packId:D})";
        using var response = await SendAsync(HttpMethod.Patch, url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string[]> GetDeliveryNoteUrlsAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.UtcDateTime.Date.AddDays(1).ToString("yyyy-MM-dd");

        var filter = $"_mb_deliveryroute_value eq {routeId:D}" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z" +
                     $" and mb_url ne null and mb_url ne ''";

        var nextUrl = (string?)$"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes?$filter={Uri.EscapeDataString(filter)}&$select=mb_url";
        var urls = new List<string>();

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var response = await SendAsync(HttpMethod.Get, nextUrl, body: null, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("value", out var valueProp))
            {
                urls.AddRange(
                    valueProp
                        .EnumerateArray()
                        .Select(e => e.GetProperty("mb_url").GetString())
                        .Where(u => !string.IsNullOrWhiteSpace(u))!
                        .Select(u => u!));
            }

            nextUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp)
                ? nextLinkProp.GetString()
                : null;
        }

        return urls.ToArray();
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
            ["mb_statusreason"] = DeliveryPackStatus.Complete,
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
            ["mb_statusreason"] = DeliveryPackStatus.Failed,
            ["mb_log"] = log
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverypacks({packId:D})";
        using var response = await SendAsync(HttpMethod.Patch, url, body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseContent = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);

            throw new HttpRequestException(
                $"Failed to update delivery pack '{packId:D}' to Failed. " +
                $"Dataverse returned {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"Response: {responseContent}",
                null,
                response.StatusCode);
        }
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
