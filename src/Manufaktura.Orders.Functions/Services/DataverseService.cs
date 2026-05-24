using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Azure.Core;
using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Services;

public class DataverseService : IDataverseService
{
    private const int MaximumEffectiveDriverRefreshOrders = 500;

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly string _dataverseUrl;
    private readonly string[] _scopes;
    private readonly ILogger<DataverseService> _logger;

    public DataverseService(HttpClient httpClient, TokenCredential credential, IConfiguration configuration, ILogger<DataverseService> logger)
    {
        _httpClient = httpClient;
        _credential = credential;
        _logger = logger;
        _dataverseUrl = (configuration["DataverseUrl"]
            ?? throw new InvalidOperationException("DataverseUrl configuration is required."))
            .TrimEnd('/');
        _scopes = [_dataverseUrl + "/.default"];
    }

    public async Task<EffectiveDriverResolutionRequest> GetEffectiveDriverResolutionRequestForOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var orderUrl = $"{_dataverseUrl}/api/data/v9.2/mb_orders({orderId:D})?$select=mb_deliverydate,_mb_customer_value,_mb_homedeliveryroute_value";
        using var orderResponse = await SendAsync(HttpMethod.Get, orderUrl, body: null, cancellationToken);
        orderResponse.EnsureSuccessStatusCode();

        using var orderDoc = await JsonDocument.ParseAsync(await orderResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var order = orderDoc.RootElement;
        var accountId = GetRequiredLookupValue(order, "_mb_customer_value", $"Order '{orderId:D}' has no customer assigned.");
        var deliveryDate = GetRequiredDateOnly(order, "mb_deliverydate", $"Order '{orderId:D}' has no delivery date assigned.");
        var routeId = GetLookupValue(order, "_mb_homedeliveryroute_value")
            ?? await GetAccountHomeDeliveryRouteAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Order '{orderId:D}' and account '{accountId:D}' have no home delivery route assigned.");

        var routeSchedule = await GetRouteDriverScheduleAsync(routeId, cancellationToken);
        var accountOverrides = await GetAccountDeliveryOverridesAsync(accountId, deliveryDate, cancellationToken);
        var driverAbsences = await GetDriverAbsencesAsync(
            deliveryDate,
            GetCandidateDriverIds(routeSchedule, accountOverrides),
            cancellationToken);

        return new EffectiveDriverResolutionRequest(accountId, deliveryDate, routeSchedule, accountOverrides, driverAbsences);
    }

    public async Task<IReadOnlyCollection<Guid>> GetOrderIdsForEffectiveDriverRefreshAsync(EffectiveDriverRefreshQuery query, CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(query.MaxOrders, 1, MaximumEffectiveDriverRefreshOrders);
        var conditions = new StringBuilder();
        conditions.AppendLine($"                  <condition attribute='mb_deliverydate' operator='on-or-after' value='{query.FromDate:yyyy-MM-dd}' />");

        if (query.ToDate is not null)
            conditions.AppendLine($"                  <condition attribute='mb_deliverydate' operator='on-or-before' value='{query.ToDate:yyyy-MM-dd}' />");

        if (query.AccountId is not null)
            conditions.AppendLine($"                  <condition attribute='mb_customer' operator='eq' value='{query.AccountId.Value:D}' />");

        if (query.RouteId is not null)
            conditions.AppendLine($"                  <condition attribute='mb_homedeliveryroute' operator='eq' value='{query.RouteId.Value:D}' />");

        if (query.DriverId is not null)
            conditions.AppendLine($"                  <condition attribute='mb_effectivedriver' operator='eq' value='{query.DriverId.Value:D}' />");

        var fetchXml =
            $"""
            <fetch count='{count}'>
              <entity name='mb_order'>
                <attribute name='mb_orderid' />
                <order attribute='mb_deliverydate' descending='false' />
                <filter type='and'>
            {conditions}                </filter>
              </entity>
            </fetch>
            """;

        var nextUrl = (string?)$"{_dataverseUrl}/api/data/v9.2/mb_orders?fetchXml={Uri.EscapeDataString(fetchXml)}";
        var orderIds = new List<Guid>();

        while (!string.IsNullOrWhiteSpace(nextUrl) && orderIds.Count < count)
        {
            using var response = await SendAsync(HttpMethod.Get, nextUrl, body: null, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var row in values.EnumerateArray())
                {
                    if (row.TryGetProperty("mb_orderid", out var idProperty) && Guid.TryParse(idProperty.GetString(), out var orderId))
                        orderIds.Add(orderId);

                    if (orderIds.Count == count)
                        break;
                }
            }

            nextUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp)
                ? nextLinkProp.GetString()
                : null;
        }

        return orderIds;
    }

    public async Task UpdateOrderEffectiveDriverAsync(Guid orderId, EffectiveDriverResolutionResult resolution, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_effectivedriversource"] = resolution.Source.ToString(),
            ["mb_effectivedriver@odata.bind"] = resolution.DriverId is Guid driverId
                ? $"/contacts({driverId:D})"
                : null
        };

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_orders({orderId:D})";
        using var response = await SendAsync(HttpMethod.Patch, url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
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
                  <condition attribute='mb_deliverydate' operator='lt' value='{dateTo}' />
                </filter>
                <link-entity name='account' from='accountid' to='mb_customer' link-type='inner'>
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

    public async Task<int> CountTotalDeliveryNotesAsync(Guid routeId, DateTimeOffset deliveryDate, CancellationToken cancellationToken = default)
    {
        var dateFrom = deliveryDate.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var dateTo = deliveryDate.UtcDateTime.Date.AddDays(1).ToString("yyyy-MM-dd");

        var filter = $"_mb_deliveryroute_value eq {routeId:D}" +
                     $" and mb_deliverydate ge {dateFrom}T00:00:00Z and mb_deliverydate lt {dateTo}T00:00:00Z";

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliverynotes?$filter={Uri.EscapeDataString(filter)}&$count=true&$select=mb_deliverynoteid&$top=1";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("@odata.count", out var countProp) ? countProp.GetInt32() : 0;
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

    public async Task<(Guid packId, bool created)> CreateDeliveryPackAsync(Guid routeId, DateTimeOffset deliveryDate, int notesCount, string? packName, CancellationToken cancellationToken = default)
    {
        // OData bind syntax for lookup fields uses a special key name that contains '@'.
        // Anonymous types cannot have such property names, so we use a dictionary.
        var body = new Dictionary<string, object?>
        {
            ["mb_deliverydate"] = deliveryDate.UtcDateTime.Date.ToString("yyyy-MM-dd"),
            ["mb_statusreason"] = DeliveryPackStatus.Generating,
            ["mb_notescount"] = notesCount,
            ["mb_deliveryroute@odata.bind"] = $"/mb_deliveryroutes({routeId:D})"
        };
        if (!string.IsNullOrWhiteSpace(packName))
            body["mb_name"] = packName;

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

    public async Task SetDeliveryPackGeneratingAsync(Guid packId, int notesCount, string? packName, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["mb_statusreason"] = DeliveryPackStatus.Generating,
            ["mb_notescount"] = notesCount
        };
        if (!string.IsNullOrWhiteSpace(packName))
            body["mb_name"] = packName;

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
        var root = doc.RootElement;
        return root.TryGetProperty("mb_name", out var nameProp) ? nameProp.GetString() : null;
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

    private async Task<Guid?> GetAccountHomeDeliveryRouteAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var url = $"{_dataverseUrl}/api/data/v9.2/accounts({accountId:D})?$select=_mb_deliveryroute_value";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return GetLookupValue(doc.RootElement, "_mb_deliveryroute_value");
    }

    private async Task<RouteDriverSchedule> GetRouteDriverScheduleAsync(Guid routeId, CancellationToken cancellationToken)
    {
        const string select = "_mb_driver_value,_mb_drivermonday_value,_mb_drivertuesday_value,_mb_driverwednesday_value,_mb_driverthursday_value,_mb_driverfriday_value,_mb_driversaturday_value,_mb_driversunday_value";
        var url = $"{_dataverseUrl}/api/data/v9.2/mb_deliveryroutes({routeId:D})?$select={select}";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var route = doc.RootElement;

        var weekdayDriverIds = new Dictionary<DayOfWeek, Guid?>
        {
            [DayOfWeek.Monday] = GetLookupValue(route, "_mb_drivermonday_value"),
            [DayOfWeek.Tuesday] = GetLookupValue(route, "_mb_drivertuesday_value"),
            [DayOfWeek.Wednesday] = GetLookupValue(route, "_mb_driverwednesday_value"),
            [DayOfWeek.Thursday] = GetLookupValue(route, "_mb_driverthursday_value"),
            [DayOfWeek.Friday] = GetLookupValue(route, "_mb_driverfriday_value"),
            [DayOfWeek.Saturday] = GetLookupValue(route, "_mb_driversaturday_value"),
            [DayOfWeek.Sunday] = GetLookupValue(route, "_mb_driversunday_value")
        };

        return new RouteDriverSchedule(GetLookupValue(route, "_mb_driver_value"), weekdayDriverIds);
    }

    private async Task<IReadOnlyCollection<AccountDeliveryOverrideRecord>> GetAccountDeliveryOverridesAsync(Guid accountId, DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        var date = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fetchXml =
            $"""
            <fetch>
              <entity name='mb_accountdeliveryoverride'>
                <attribute name='mb_accountdeliveryoverrideid' />
                <attribute name='mb_account' />
                <attribute name='mb_driver' />
                <attribute name='mb_fromdate' />
                <attribute name='mb_todate' />
                <filter type='and'>
                  <condition attribute='statecode' operator='eq' value='0' />
                  <condition attribute='mb_account' operator='eq' value='{accountId:D}' />
                  <condition attribute='mb_fromdate' operator='on-or-before' value='{date}' />
                  <condition attribute='mb_todate' operator='on-or-after' value='{date}' />
                </filter>
              </entity>
            </fetch>
            """;

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_accountdeliveryoverrides?fetchXml={Uri.EscapeDataString(fetchXml)}";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("value", out var values))
            return [];

        var overrides = new List<AccountDeliveryOverrideRecord>();
        foreach (var row in values.EnumerateArray())
        {
            var driverId = GetRequiredLookupValue(row, "_mb_driver_value", "Account delivery override has no driver assigned.");
            var fromDate = GetRequiredDateOnly(row, "mb_fromdate", "Account delivery override has no from date.");
            var toDate = GetRequiredDateOnly(row, "mb_todate", "Account delivery override has no to date.");
            overrides.Add(new AccountDeliveryOverrideRecord(accountId, fromDate, toDate, driverId));
        }

        return overrides;
    }

    private async Task<IReadOnlyCollection<DriverAbsenceRecord>> GetDriverAbsencesAsync(DateOnly deliveryDate, IReadOnlyCollection<Guid> driverIds, CancellationToken cancellationToken)
    {
        if (driverIds.Count == 0)
            return [];

        var date = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var driverValues = string.Join(
            Environment.NewLine,
            driverIds.Select(driverId => $"                    <value>{driverId:D}</value>"));
        var fetchXml =
            $"""
            <fetch>
              <entity name='mb_driverabsence'>
                <attribute name='mb_driverabsenceid' />
                <attribute name='mb_driver' />
                <attribute name='mb_fromdate' />
                <attribute name='mb_todate' />
                <filter type='and'>
                  <condition attribute='statecode' operator='eq' value='0' />
                  <condition attribute='mb_fromdate' operator='on-or-before' value='{date}' />
                  <condition attribute='mb_todate' operator='on-or-after' value='{date}' />
                  <condition attribute='mb_driver' operator='in'>
            {driverValues}
                  </condition>
                </filter>
              </entity>
            </fetch>
            """;

        var url = $"{_dataverseUrl}/api/data/v9.2/mb_driverabsences?fetchXml={Uri.EscapeDataString(fetchXml)}";
        using var response = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("value", out var values))
            return [];

        var absences = new List<DriverAbsenceRecord>();
        foreach (var row in values.EnumerateArray())
        {
            var driverId = GetRequiredLookupValue(row, "_mb_driver_value", "Driver absence has no driver assigned.");
            var fromDate = GetRequiredDateOnly(row, "mb_fromdate", "Driver absence has no from date.");
            var toDate = GetRequiredDateOnly(row, "mb_todate", "Driver absence has no to date.");
            absences.Add(new DriverAbsenceRecord(driverId, fromDate, toDate));
        }

        return absences;
    }

    private static Guid[] GetCandidateDriverIds(
        RouteDriverSchedule routeSchedule,
        IReadOnlyCollection<AccountDeliveryOverrideRecord> accountOverrides)
    {
        var driverIds = new HashSet<Guid>();

        if (routeSchedule.DefaultDriverId is Guid defaultDriverId)
            driverIds.Add(defaultDriverId);

        foreach (var weekdayDriverId in routeSchedule.WeekdayDriverIds.Values)
        {
            if (weekdayDriverId is Guid driverId)
                driverIds.Add(driverId);
        }

        foreach (var accountOverride in accountOverrides)
        {
            driverIds.Add(accountOverride.DriverId);
        }

        return [.. driverIds];
    }

    private static Guid GetRequiredLookupValue(JsonElement element, string propertyName, string message)
        => GetLookupValue(element, propertyName) ?? throw new InvalidOperationException(message);

    private static Guid? GetLookupValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return Guid.TryParse(property.GetString(), out var id) ? id : null;
    }

    private static DateOnly GetRequiredDateOnly(JsonElement element, string propertyName, string message)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            throw new InvalidOperationException(message);

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(message);

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;

        var dateTime = property.GetDateTimeOffset();
        return DateOnly.FromDateTime(dateTime.UtcDateTime);
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

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = response.Content is not null
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogWarning(
                    "Dataverse returned {StatusCode} for {Method} {Url}. Response body: {Body}",
                    (int)response.StatusCode, method.Method, url, errorBody);
            }
            else
            {
                _logger.LogError(
                    "Dataverse returned {StatusCode} for {Method} {Url}. Response body: {Body}",
                    (int)response.StatusCode, method.Method, url, errorBody);
            }
        }

        return response;
    }
}
