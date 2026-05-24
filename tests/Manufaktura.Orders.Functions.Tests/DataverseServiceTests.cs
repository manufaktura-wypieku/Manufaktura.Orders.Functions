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
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Conflict)); // POST -> 409
        handler.Enqueue(OkJson($$"""{"value":[{"mb_deliverypackid":"{{existingPackId:D}}","mb_statusreason":1}]}""")); // GET re-query -> 200

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
    public async Task GetEffectiveDriverResolutionRequestForOrder_LoadsOrderRouteOverridesAndAbsences()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var accountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var routeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var defaultDriverId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var fridayDriverId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var overrideDriverId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var absentDriverId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""
        {
          "_mb_customer_value":"{{accountId:D}}",
          "_mb_homedeliveryroute_value":"{{routeId:D}}",
          "mb_deliverydate":"2026-05-08"
        }
        """));
        handler.Enqueue(OkJson($$"""
        {
          "_mb_driver_value":"{{defaultDriverId:D}}",
          "_mb_driverfriday_value":"{{fridayDriverId:D}}"
        }
        """));
        handler.Enqueue(OkJson($$"""
        {
          "value":[
            {
              "_mb_driver_value":"{{overrideDriverId:D}}",
              "mb_fromdate":"2026-05-08",
              "mb_todate":"2026-05-15"
            }
          ]
        }
        """));
        handler.Enqueue(OkJson($$"""
        {
          "value":[
            {
              "_mb_driver_value":"{{absentDriverId:D}}",
              "mb_fromdate":"2026-05-01",
              "mb_todate":"2026-05-10"
            }
          ]
        }
        """));

        var service = CreateService(handler);
        var request = await service.GetEffectiveDriverResolutionRequestForOrderAsync(orderId, TestContext.Current.CancellationToken);

        Assert.Equal(accountId, request.AccountId);
        Assert.Equal(new DateOnly(2026, 5, 8), request.DeliveryDate);
        Assert.Equal(defaultDriverId, request.RouteSchedule.DefaultDriverId);
        Assert.Equal(fridayDriverId, request.RouteSchedule.GetWeekdayDriver(DayOfWeek.Friday));

        var accountOverride = Assert.Single(request.AccountOverrides);
        Assert.Equal(accountId, accountOverride.AccountId);
        Assert.Equal(overrideDriverId, accountOverride.DriverId);

        var absence = Assert.Single(request.DriverAbsences);
        Assert.Equal(absentDriverId, absence.DriverId);

        Assert.Equal(4, handler.SentRequests.Count);
        Assert.Contains($"mb_orders({orderId:D})", handler.SentRequests[0].Url);
        Assert.Contains($"mb_deliveryroutes({routeId:D})", handler.SentRequests[1].Url);
        Assert.Contains("mb_accountdeliveryoverrides", handler.SentRequests[2].Url);
        Assert.Contains("mb_driverabsences", handler.SentRequests[3].Url);
    }

    [Fact]
    public async Task GetEffectiveDriverResolutionRequestForOrder_FallsBackToAccountRouteWhenOrderHomeRouteIsBlank()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var accountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var routeId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""
        {
          "_mb_customer_value":"{{accountId:D}}",
          "_mb_homedeliveryroute_value":null,
          "mb_deliverydate":"2026-05-08"
        }
        """));
        handler.Enqueue(OkJson($$"""{"_mb_deliveryroute_value":"{{routeId:D}}"}"""));
        handler.Enqueue(OkJson("""{"_mb_driver_value":null}"""));
        handler.Enqueue(OkJson("""{"value":[]}"""));
        handler.Enqueue(OkJson("""{"value":[]}"""));

        var service = CreateService(handler);
        var request = await service.GetEffectiveDriverResolutionRequestForOrderAsync(orderId, TestContext.Current.CancellationToken);

        Assert.Equal(accountId, request.AccountId);
        Assert.Equal(new DateOnly(2026, 5, 8), request.DeliveryDate);
        Assert.Equal(5, handler.SentRequests.Count);
        Assert.Contains($"accounts({accountId:D})", handler.SentRequests[1].Url);
        Assert.Contains($"mb_deliveryroutes({routeId:D})", handler.SentRequests[2].Url);
    }

    [Fact]
    public async Task GetOrderIdsForEffectiveDriverRefresh_EmbedsFiltersAndFollowsNextLink()
    {
        var accountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var routeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var driverId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var firstOrderId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var secondOrderId = Guid.Parse("66666666-6666-6666-6666-666666666666");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""
        {
          "value":[{"mb_orderid":"{{firstOrderId:D}}"}],
          "@odata.nextLink":"{{DataverseUrl}}/api/data/v9.2/mb_orders?$skiptoken=page2"
        }
        """));
        handler.Enqueue(OkJson($$"""
        {
          "value":[{"mb_orderid":"{{secondOrderId:D}}"}]
        }
        """));

        var service = CreateService(handler);
        var query = new EffectiveDriverRefreshQuery(
            new DateOnly(2026, 5, 8),
            new DateOnly(2026, 5, 12),
            accountId,
            routeId,
            driverId,
            50);

        var orderIds = await service.GetOrderIdsForEffectiveDriverRefreshAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal([firstOrderId, secondOrderId], orderIds);
        Assert.Equal(2, handler.SentRequests.Count);

        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("<fetch count='50'>", decodedUrl);
        Assert.Contains("attribute='mb_deliverydate' operator='on-or-after' value='2026-05-08'", decodedUrl);
        Assert.Contains("attribute='mb_deliverydate' operator='on-or-before' value='2026-05-12'", decodedUrl);
        Assert.Contains($"attribute='mb_customer' operator='eq' value='{accountId:D}'", decodedUrl);
        Assert.Contains($"attribute='mb_homedeliveryroute' operator='eq' value='{routeId:D}'", decodedUrl);
        Assert.Contains($"attribute='mb_effectivedriver' operator='eq' value='{driverId:D}'", decodedUrl);
        Assert.Equal(HttpMethod.Get, handler.SentRequests[1].Method);
        Assert.Contains("skiptoken=page2", handler.SentRequests[1].Url);
    }

    [Fact]
    public async Task UpdateOrderEffectiveDriver_SetsDriverLookupAndSource()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var driverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        var service = CreateService(handler);
        await service.UpdateOrderEffectiveDriverAsync(orderId, new EffectiveDriverResolutionResult(driverId, EffectiveDriverSource.RouteWeekday), TestContext.Current.CancellationToken);

        Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Patch, handler.SentRequests[0].Method);
        Assert.Contains($"mb_orders({orderId:D})", handler.SentRequests[0].Url);
        Assert.Contains("\"mb_effectivedriversource\":\"RouteWeekday\"", handler.SentRequests[0].Body);
        Assert.Contains($"\"mb_effectivedriver@odata.bind\":\"/contacts({driverId:D})\"", handler.SentRequests[0].Body);
    }

    [Fact]
    public async Task UpdateOrderEffectiveDriver_ClearsDriverLookupWhenUncovered()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        var service = CreateService(handler);
        await service.UpdateOrderEffectiveDriverAsync(orderId, new EffectiveDriverResolutionResult(null, EffectiveDriverSource.MissingRouteDriver), TestContext.Current.CancellationToken);

        Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Patch, handler.SentRequests[0].Method);
        Assert.Contains("\"mb_effectivedriversource\":\"MissingRouteDriver\"", handler.SentRequests[0].Body);
        Assert.Contains("\"mb_effectivedriver@odata.bind\":null", handler.SentRequests[0].Body);
    }
}
