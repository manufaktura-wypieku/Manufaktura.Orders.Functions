using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace Manufaktura.Orders.Functions.IntegrationTests;

public sealed class DataverseTestClient(HttpClient httpClient, TokenCredential credential, string dataverseUrl)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dataverseUrl = dataverseUrl.TrimEnd('/');
    private readonly Uri _baseApiUri = new($"{dataverseUrl.TrimEnd('/')}/api/data/v9.2/");
    private readonly string[] _scopes = [$"{dataverseUrl.TrimEnd('/')}/.default"];

    public async Task<string?> GetEnvironmentVariableValueAsync(string schemaName, CancellationToken cancellationToken)
    {
        var escapedSchemaName = schemaName.Replace("'", "''", StringComparison.Ordinal);
        var filter = Uri.EscapeDataString($"schemaname eq '{escapedSchemaName}'");
        var query = "environmentvariabledefinitions" +
                    "?$select=schemaname,defaultvalue,type,secretstore" +
                    $"&$filter={filter}" +
                    "&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)";

        using var document = await GetJsonAsync(query, cancellationToken);
        var definitions = document.RootElement.GetProperty("value");
        if (definitions.GetArrayLength() == 0)
            return null;

        var definition = definitions[0];
        if (definition.TryGetProperty("environmentvariabledefinition_environmentvariablevalue", out var values))
        {
            var configuredValue = values
                .EnumerateArray()
                .Select(value => value.TryGetProperty("value", out var valueProperty) ? valueProperty.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (!string.IsNullOrWhiteSpace(configuredValue))
                return configuredValue;
        }

        return definition.TryGetProperty("defaultvalue", out var defaultValue) ? defaultValue.GetString() : null;
    }

    public async Task<Guid?> GetFirstReusablePriceListIdAsync(CancellationToken cancellationToken)
    {
        const string query = "accounts?$select=_mb_pricelist_value&$filter=statecode eq 0 and _mb_pricelist_value ne null&$top=1";
        using var document = await GetJsonAsync(query, cancellationToken);
        var values = document.RootElement.GetProperty("value");
        if (values.GetArrayLength() == 0)
            return null;

        return GetLookupValue(values[0], "_mb_pricelist_value");
    }

    public async Task<Guid> CreateEntityAsync(string entitySetName, IReadOnlyDictionary<string, object?> body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, entitySetName, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync(response, $"Failed to create {entitySetName}", cancellationToken);

        if (!response.Headers.TryGetValues("OData-EntityId", out var values))
            throw new InvalidOperationException($"Dataverse did not return OData-EntityId after creating {entitySetName}.");

        var entityId = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(entityId))
            throw new InvalidOperationException($"Dataverse returned a blank OData-EntityId after creating {entitySetName}.");

        var match = Regex.Match(entityId, @"\(([a-f0-9\-]{36})\)", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException($"Could not parse created Dataverse ID from OData-EntityId '{entityId}'.");

        return Guid.Parse(match.Groups[1].Value);
    }

    public async Task PatchEntityAsync(string entitySetName, Guid id, IReadOnlyDictionary<string, object?> body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"{entitySetName}({id:D})", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync(response, $"Failed to patch {entitySetName}({id:D})", cancellationToken);
    }

    public async Task DeleteEntityAsync(string entitySetName, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"{entitySetName}({id:D})", body: null, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw await CreateRequestExceptionAsync(response, $"Failed to delete {entitySetName}({id:D})", cancellationToken);
        }
        catch
        {
            // Cleanup is best effort. The test failure should describe the original problem, not a cleanup race.
        }
    }

    public async Task DeleteOrderItemsForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString($"_mb_order_value eq {orderId:D}");
        var itemIds = await ListIdsAsync($"mb_orderitems?$select=mb_orderitemid&$filter={filter}", "mb_orderitemid", cancellationToken);

        foreach (var itemId in itemIds)
            await DeleteEntityAsync("mb_orderitems", itemId, cancellationToken);
    }

    public async Task<OrderDriverSnapshot> GetOrderDriverSnapshotAsync(Guid orderId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"mb_orders({orderId:D})?$select=_mb_homedeliveryroute_value,_mb_effectivedriver_value,mb_effectivedriversource",
            cancellationToken);

        var root = document.RootElement;
        return new OrderDriverSnapshot(
            GetLookupValue(root, "_mb_homedeliveryroute_value"),
            GetLookupValue(root, "_mb_effectivedriver_value"),
            root.TryGetProperty("mb_effectivedriversource", out var source) ? source.GetString() : null);
    }

    public async Task<JsonDocument> GetJsonAsync(string relativeOrAbsoluteUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, relativeOrAbsoluteUrl, body: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync(response, $"Dataverse GET failed for {relativeOrAbsoluteUrl}", cancellationToken);

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyCollection<Guid>> ListIdsAsync(string relativeOrAbsoluteUrl, string idPropertyName, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        var nextUrl = (string?)relativeOrAbsoluteUrl;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var document = await GetJsonAsync(nextUrl, cancellationToken);
            if (document.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var row in values.EnumerateArray())
                {
                    if (row.TryGetProperty(idPropertyName, out var idProperty) && Guid.TryParse(idProperty.GetString(), out var id))
                        ids.Add(id);
                }
            }

            nextUrl = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) ? nextLink.GetString() : null;
        }

        return ids;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeOrAbsoluteUrl, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, CreateUri(relativeOrAbsoluteUrl));
        request.Headers.Authorization = await GetAuthHeaderAsync(cancellationToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<AuthenticationHeaderValue> GetAuthHeaderAsync(CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken);
        return new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private Uri CreateUri(string relativeOrAbsoluteUrl)
        => Uri.TryCreate(relativeOrAbsoluteUrl, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri
            : new Uri(_baseApiUri, relativeOrAbsoluteUrl);

    private static Guid? GetLookupValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return Guid.TryParse(property.GetString(), out var id) ? id : null;
    }

    private static async Task<HttpRequestException> CreateRequestExceptionAsync(HttpResponseMessage response, string message, CancellationToken cancellationToken)
    {
        var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpRequestException($"{message}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}", null, response.StatusCode);
    }
}

public sealed record OrderDriverSnapshot(Guid? HomeDeliveryRouteId, Guid? EffectiveDriverId, string? EffectiveDriverSource);