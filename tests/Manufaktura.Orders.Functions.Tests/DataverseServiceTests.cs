using System.Net;
using System.Text;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class DataverseServiceTests
{
    private const string DataverseUrl = "https://org9999999.crm.dynamics.com";

    private static DataverseService CreateService(FakeHttpMessageHandler handler)
    {
        var config = Substitute.For<IConfiguration>();
        config["DataverseUrl"].Returns(DataverseUrl);
        return new DataverseService(new HttpClient(handler), new FakeTokenCredential(), config, Substitute.For<ILogger<DataverseService>>());
    }

    private static HttpResponseMessage OkJson(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---

    [Fact]
    public async Task CountCompletedOrders_EmbedsFetchXmlWithRouteIdAndDateRange()
    {
        var routeId = Guid.Parse("12345678-0000-0000-0000-000000000001");
        var date = new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero);

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[{"ordercount":7}]}"""));

        var service = CreateService(handler);
        var count = await service.CountCompletedOrdersByRouteAndDateAsync(routeId, date);

        Assert.Equal(7, count);
        Assert.Single(handler.SentRequests);
        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("fetchXml=", handler.SentRequests[0].Url);
        Assert.Contains("12345678-0000-0000-0000-000000000001", decodedUrl);
        Assert.Contains("2026-04-06", decodedUrl);
        Assert.Contains("2026-04-07", decodedUrl); // date range end = date + 1 day
        Assert.Contains("to='mb_customer'", decodedUrl); // logical name, not mb_customerid
    }

    [Fact]
    public async Task CountCompletedOrders_ParsesStringCountFromAggregateResponse()
    {
        // Dataverse aggregate queries can return numeric values as JSON strings.
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[{"ordercount":"3"}]}"""));

        var service = CreateService(handler);
        var count = await service.CountCompletedOrdersByRouteAndDateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task CountTotalDeliveryNotes_EmbedsRouteIdAndDateRangeInFilter()
    {
        var routeId = Guid.Parse("12345678-0000-0000-0000-000000000002");
        var date = new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero);

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"@odata.count":5,"value":[]}"""));

        var service = CreateService(handler);
        var count = await service.CountTotalDeliveryNotesAsync(routeId, date);

        Assert.Equal(5, count);
        Assert.Single(handler.SentRequests);
        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("mb_deliverynotes", decodedUrl);
        Assert.Contains("12345678-0000-0000-0000-000000000002", decodedUrl);
        Assert.Contains("2026-04-06", decodedUrl);
        Assert.Contains("2026-04-07", decodedUrl); // date range end = date + 1 day
        Assert.Contains("$count=true", decodedUrl);
        Assert.DoesNotContain("mb_url", decodedUrl); // total count must not filter by URL presence
    }

    [Fact]
    public async Task CountTotalDeliveryNotes_ReturnsZeroWhenODataCountMissing()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[]}"""));

        var service = CreateService(handler);
        var count = await service.CountTotalDeliveryNotesAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetDeliveryNoteUrls_FollowsNextLinkAndAggregatesAllPages()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[{"mb_url":"https://sp.com/doc1.pdf"}],"@odata.nextLink":"https://org.crm/next-page"}"""));
        handler.Enqueue(OkJson("""{"value":[{"mb_url":"https://sp.com/doc2.pdf"}]}"""));

        var service = CreateService(handler);
        var urls = await service.GetDeliveryNoteUrlsAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(2, handler.SentRequests.Count);
        Assert.Equal("https://org.crm/next-page", handler.SentRequests[1].Url);
        Assert.Equal(["https://sp.com/doc1.pdf", "https://sp.com/doc2.pdf"], urls);
    }

    [Fact]
    public async Task CreateDeliveryPack_Returns201_IsCreatedTrue()
    {
        var newPackId = Guid.Parse("aaaaaaaa-0001-0001-0001-000000000001");
        var response201 = new HttpResponseMessage(HttpStatusCode.Created);
        response201.Headers.TryAddWithoutValidation("OData-EntityId",
            $"{DataverseUrl}/api/data/v9.2/mb_deliverypacks({newPackId:D})");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(response201);

        var service = CreateService(handler);
        var (packId, created) = await service.CreateDeliveryPackAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 5, "Test Pack");

        Assert.True(created);
        Assert.Equal(newPackId, packId);
        Assert.Contains("\"mb_name\":\"Test Pack\"", handler.SentRequests[0].Body);
    }

    [Fact]
    public async Task CreateDeliveryPack_Returns409Conflict_IsCreatedFalse()
    {
        var existingPackId = Guid.Parse("bbbbbbbb-0002-0002-0002-000000000002");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Conflict)); // POST → 409
        handler.Enqueue(OkJson($$"""{"value":[{"mb_deliverypackid":"{{existingPackId:D}}","mb_statusreason":1}]}""")); // GET re-query → 200

        var service = CreateService(handler);
        var (packId, created) = await service.CreateDeliveryPackAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 5, "Test Pack");

        Assert.False(created);
        Assert.Equal(existingPackId, packId);
        Assert.Equal(2, handler.SentRequests.Count);
        Assert.Equal(HttpMethod.Post, handler.SentRequests[0].Method);
        Assert.Contains("\"mb_name\":\"Test Pack\"", handler.SentRequests[0].Body);
        Assert.Equal(HttpMethod.Get, handler.SentRequests[1].Method);
    }

    [Fact]
    public async Task GetDeliveryNote_ThrowsMissingDeliveryRouteException_WhenRouteValueIsNull()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""{"_mb_deliveryroute_value":null,"mb_deliverydate":"2026-04-07T00:00:00Z"}"""));

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<MissingDeliveryRouteException>(() => service.GetDeliveryNoteAsync(noteId));

        Assert.Equal(noteId, ex.DeliveryNoteId);
        Assert.Contains("has no delivery route assigned", ex.Message);
    }

    [Fact]
    public async Task CreateDeliveryPack_NullPackName_OmitsMbNameFromBody()
    {
        var newPackId = Guid.Parse("aaaaaaaa-0001-0001-0001-000000000001");
        var response201 = new HttpResponseMessage(HttpStatusCode.Created);
        response201.Headers.TryAddWithoutValidation("OData-EntityId",
            $"{DataverseUrl}/api/data/v9.2/mb_deliverypacks({newPackId:D})");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(response201);

        var service = CreateService(handler);
        var (packId, created) = await service.CreateDeliveryPackAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 5, null);

        Assert.True(created);
        Assert.DoesNotContain("mb_name", handler.SentRequests[0].Body);
    }

    [Fact]
    public async Task GetDeliveryNote_ThrowsMissingDeliveryRouteException_WhenRouteValueIsEmptyString()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""{"_mb_deliveryroute_value":"","mb_deliverydate":"2026-04-07T00:00:00Z"}"""));

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<MissingDeliveryRouteException>(() => service.GetDeliveryNoteAsync(noteId));

        Assert.Equal(noteId, ex.DeliveryNoteId);
        Assert.Contains("has no delivery route assigned", ex.Message);
    }

    [Fact]
    public async Task ListActiveAccounts_FiltersByOrderOnDaysValue_AndFollowsNextLink()
    {
        var thursday = new DateOnly(2026, 9, 17);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""
            {
              "value":[{"accountid":"11111111-1111-1111-1111-111111111111","name":"The Bakery","_mb_pricelist_value":"33333333-3333-3333-3333-333333333333"}],
              "@odata.nextLink":"https://org.crm/accounts-page-2"
            }
            """));
        handler.Enqueue(OkJson("""
            {"value":[{"accountid":"22222222-2222-2222-2222-222222222222","name":null,"_mb_pricelist_value":null}]}
            """));

        var service = CreateService(handler);
        var accounts = await service.ListActiveAccountsForDeliveryDateAsync(thursday);

        Assert.Equal(2, accounts.Count);
        Assert.Equal("The Bakery", accounts[0].Name);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), accounts[0].PriceListId);
        Assert.Null(accounts[1].PriceListId);
        Assert.Equal("https://org.crm/accounts-page-2", handler.SentRequests[1].Url);

        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("statecode eq 0", decodedUrl);
        Assert.Contains("PropertyValues=['7']", decodedUrl);
    }

    [Fact]
    public async Task ListAccountIdsWithOrder_DoesNotFilterByState_AndUsesDeliveryDateWindow()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""
            {"value":[{"_mb_customer_value":"11111111-1111-1111-1111-111111111111"},{"_mb_customer_value":null}]}
            """));

        var service = CreateService(handler);
        var ids = await service.ListAccountIdsWithOrderOnDateAsync(new DateOnly(2026, 9, 18));

        Assert.Equal([Guid.Parse("11111111-1111-1111-1111-111111111111")], ids);
        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("mb_orders", decodedUrl);
        Assert.Contains("2026-09-18T00:00:00Z", decodedUrl);
        Assert.Contains("2026-09-19T00:00:00Z", decodedUrl);
        Assert.Contains("mb_order_kind eq 124530000", decodedUrl);
        Assert.Contains("mb_order_kind eq null", decodedUrl);
        Assert.DoesNotContain("statecode", decodedUrl);
    }

    [Fact]
    public async Task CreateEmptyOrder_BindsAccountPriceListAndDeliveryDate()
    {
        var accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var priceListId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        var service = CreateService(handler);
        await service.CreateEmptyOrderAsync(accountId, priceListId, new DateOnly(2026, 9, 18));

        var request = handler.SentRequests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("/api/data/v9.2/mb_orders", request.Url);
        Assert.Contains("\"mb_deliverydate\":\"2026-09-18\"", request.Body);
        Assert.Contains("\"mb_order_kind\":124530000", request.Body);
        Assert.Contains($"\"mb_Customer_account@odata.bind\":\"/accounts({accountId:D})\"", request.Body);
        Assert.Contains($"\"mb_Pricelist@odata.bind\":\"/mb_pricelists({priceListId:D})\"", request.Body);
    }
}
