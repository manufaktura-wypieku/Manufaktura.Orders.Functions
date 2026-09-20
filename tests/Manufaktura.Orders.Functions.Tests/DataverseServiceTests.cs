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
    public async Task CountTotalDeliveryNotesForDriverAndDate_EmbedsFetchXmlWithDriverIdAndDateRange()
    {
        var driverId = Guid.Parse("12345678-0000-0000-0000-000000000001");
        var date = new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero);

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[{"notecount":7}]}"""));

        var service = CreateService(handler);
        var count = await service.CountTotalDeliveryNotesForDriverAndDateAsync(driverId, date);

        Assert.Equal(7, count);
        Assert.Single(handler.SentRequests);
        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("fetchXml=", handler.SentRequests[0].Url);
        Assert.Contains("mb_deliverynote", decodedUrl);
        Assert.Contains("12345678-0000-0000-0000-000000000001", decodedUrl);
        Assert.Contains("attribute='mb_effectivedriver' operator='eq'", decodedUrl);
        Assert.Contains("to='mb_order'", decodedUrl);
        Assert.Contains("2026-04-06", decodedUrl);
        Assert.Contains("2026-04-07", decodedUrl);
        Assert.DoesNotContain("attribute='mb_url'", decodedUrl);
    }

    [Fact]
    public async Task CountDeliveryNotesWithUrlForDriverAndDate_EmbedsUrlFilterAndParsesStringCount()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[{"notecount":"3"}]}"""));

        var service = CreateService(handler);
        var count = await service.CountDeliveryNotesWithUrlForDriverAndDateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(3, count);
        var decodedUrl = Uri.UnescapeDataString(handler.SentRequests[0].Url);
        Assert.Contains("attribute='mb_url' operator='not-null'", decodedUrl);
        Assert.Contains("attribute='mb_url' operator='ne' value=''", decodedUrl);
    }

    [Fact]
    public async Task CountTotalDeliveryNotesForDriverAndDate_ReturnsZeroWhenAggregateValueMissing()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[]}"""));

        var service = CreateService(handler);
        var count = await service.CountTotalDeliveryNotesForDriverAndDateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetDeliveryNoteUrlsForDriverAndDate_FollowsNextLinkAndAggregatesAllPages()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson("""{"value":[{"mb_url":"https://sp.com/doc1.pdf"}],"@odata.nextLink":"https://org.crm/next-page"}"""));
        handler.Enqueue(OkJson("""{"value":[{"mb_url":"https://sp.com/doc2.pdf"}]}"""));

        var service = CreateService(handler);
        var urls = await service.GetDeliveryNoteUrlsForDriverAndDateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(2, handler.SentRequests.Count);
        Assert.Equal("https://org.crm/next-page", handler.SentRequests[1].Url);
        Assert.Equal(["https://sp.com/doc1.pdf", "https://sp.com/doc2.pdf"], urls);
    }

    [Fact]
    public async Task CreateDeliveryPack_Returns201_IsCreatedTrue()
    {
        var newPackId = Guid.Parse("aaaaaaaa-0001-0001-0001-000000000001");
        var driverId = Guid.Parse("12345678-0000-0000-0000-000000000010");
        var response201 = new HttpResponseMessage(HttpStatusCode.Created);
        response201.Headers.TryAddWithoutValidation("OData-EntityId",
            $"{DataverseUrl}/api/data/v9.2/mb_deliverypacks({newPackId:D})");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(response201);

        var service = CreateService(handler);
        var (packId, created) = await service.CreateDeliveryPackAsync(driverId, DateTimeOffset.UtcNow, 5, "Test Pack");

        Assert.True(created);
        Assert.Equal(newPackId, packId);
        Assert.Contains("\"mb_name\":\"Test Pack\"", handler.SentRequests[0].Body);
        Assert.Contains($"\"mb_deliverypack_effectivedriver@odata.bind\":\"/contacts({driverId:D})\"", handler.SentRequests[0].Body);
        Assert.DoesNotContain("mb_effectivedriver@odata.bind", handler.SentRequests[0].Body);
        Assert.DoesNotContain("mb_deliveryroute@odata.bind", handler.SentRequests[0].Body);
    }

    [Fact]
    public async Task CreateDeliveryPack_Returns409Conflict_IsCreatedFalse()
    {
        var existingPackId = Guid.Parse("bbbbbbbb-0002-0002-0002-000000000002");
        var driverId = Guid.Parse("12345678-0000-0000-0000-000000000011");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Conflict)); // POST -> 409
        handler.Enqueue(OkJson($$"""{"value":[{"mb_deliverypackid":"{{existingPackId:D}}","mb_statusreason":1}]}""")); // GET re-query -> 200

        var service = CreateService(handler);
        var (packId, created) = await service.CreateDeliveryPackAsync(driverId, DateTimeOffset.UtcNow, 5, "Test Pack");

        Assert.False(created);
        Assert.Equal(existingPackId, packId);
        Assert.Equal(2, handler.SentRequests.Count);
        Assert.Equal(HttpMethod.Post, handler.SentRequests[0].Method);
        Assert.Contains("\"mb_name\":\"Test Pack\"", handler.SentRequests[0].Body);
        Assert.Equal(HttpMethod.Get, handler.SentRequests[1].Method);
        Assert.Contains($"_mb_effectivedriver_value eq {driverId:D}", Uri.UnescapeDataString(handler.SentRequests[1].Url));
        Assert.Contains("statecode eq 0", Uri.UnescapeDataString(handler.SentRequests[1].Url));
    }

    [Fact]
    public async Task GetDeliveryNote_ReturnsOrderEffectiveDriverAndDeliveryDate()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var orderId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var driverId = Guid.Parse("cccccccc-dddd-eeee-ffff-000000000000");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""{"_mb_order_value":"{{orderId:D}}"}"""));
        handler.Enqueue(OkJson($$"""{"_mb_effectivedriver_value":"{{driverId:D}}","mb_deliverydate":"2026-04-07"}"""));

        var service = CreateService(handler);
        var note = await service.GetDeliveryNoteAsync(noteId);

        Assert.Equal(noteId, note.Id);
        Assert.Equal(orderId, note.OrderId);
        Assert.Equal(driverId, note.EffectiveDriverId);
        Assert.Equal(new DateTimeOffset(2026, 4, 7, 0, 0, 0, TimeSpan.Zero), note.DeliveryDate);
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
    public async Task GetDeliveryNote_ThrowsMissingDeliveryPackGrouping_WhenOrderValueIsNull()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""{"_mb_order_value":null}"""));

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<MissingDeliveryPackGroupingException>(() => service.GetDeliveryNoteAsync(noteId));

        Assert.Equal(noteId, ex.DeliveryNoteId);
        Assert.Equal("missing_order", ex.Code);
    }

    [Fact]
    public async Task GetDeliveryNote_ThrowsMissingDeliveryPackGrouping_WhenOrderEffectiveDriverIsNull()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var orderId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""{"_mb_order_value":"{{orderId:D}}"}"""));
        handler.Enqueue(OkJson("""{"_mb_effectivedriver_value":null,"mb_deliverydate":"2026-04-07"}"""));

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<MissingDeliveryPackGroupingException>(() => service.GetDeliveryNoteAsync(noteId));

        Assert.Equal(noteId, ex.DeliveryNoteId);
        Assert.Equal("missing_effective_driver", ex.Code);
    }

    [Fact]
    public async Task GetDeliveryNote_ThrowsOrderNotFound_WhenLinkedOrderIsMissing()
    {
        var noteId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var orderId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(OkJson($$"""{"_mb_order_value":"{{orderId:D}}"}"""));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var service = CreateService(handler);
        var ex = await Assert.ThrowsAsync<MissingDeliveryPackGroupingException>(() => service.GetDeliveryNoteAsync(noteId));

        Assert.Equal(noteId, ex.DeliveryNoteId);
        Assert.Equal("order_not_found", ex.Code);
        Assert.Contains(orderId.ToString("D"), ex.Message);
    }

    [Fact]
    public async Task AppendDeliveryPackLog_RetriesWhenIfMatchFails()
    {
        var packId = Guid.Parse("aaaaaaaa-0001-0001-0001-000000000001");
        var firstGet = OkJson("""{"mb_log":"old"}""");
        firstGet.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-1\"");
        var secondGet = OkJson("""{"mb_log":"old\nnewer"}""");
        secondGet.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-2\"");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(firstGet);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        handler.Enqueue(secondGet);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        var service = CreateService(handler);
        await service.AppendDeliveryPackLogAsync(packId, "Superseded by delivery pack.", TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.SentRequests.Count);
        Assert.Equal("\"etag-1\"", handler.SentRequests[1].IfMatch);
        Assert.Equal("\"etag-2\"", handler.SentRequests[3].IfMatch);
        Assert.Contains("Superseded by delivery pack.", handler.SentRequests[3].Body);
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
              "_mb_driver_value":"{{fridayDriverId:D}}",
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
        Assert.Equal(routeId, request.RouteId);
        Assert.Equal(defaultDriverId, request.RouteSchedule.DefaultDriverId);
        Assert.Equal(fridayDriverId, request.RouteSchedule.GetWeekdayDriver(DayOfWeek.Friday));

        var accountOverride = Assert.Single(request.AccountOverrides);
        Assert.Equal(accountId, accountOverride.AccountId);
        Assert.Equal(overrideDriverId, accountOverride.DriverId);

        var absence = Assert.Single(request.DriverAbsences);
        Assert.Equal(fridayDriverId, absence.DriverId);

        Assert.Equal(4, handler.SentRequests.Count);
        Assert.Contains($"mb_orders({orderId:D})", handler.SentRequests[0].Url);
        Assert.Contains($"mb_deliveryroutes({routeId:D})", handler.SentRequests[1].Url);
        Assert.Contains("mb_accountdeliveryoverrides", handler.SentRequests[2].Url);
        Assert.Contains("mb_driverabsences", handler.SentRequests[3].Url);
        var decodedAbsenceUrl = Uri.UnescapeDataString(handler.SentRequests[3].Url);
        Assert.Contains("attribute='mb_driver' operator='in'", decodedAbsenceUrl);
        Assert.Contains($"<value>{defaultDriverId:D}</value>", decodedAbsenceUrl);
        Assert.Contains($"<value>{fridayDriverId:D}</value>", decodedAbsenceUrl);
        Assert.Contains($"<value>{overrideDriverId:D}</value>", decodedAbsenceUrl);
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

        var service = CreateService(handler);
        var request = await service.GetEffectiveDriverResolutionRequestForOrderAsync(orderId, TestContext.Current.CancellationToken);

        Assert.Equal(accountId, request.AccountId);
        Assert.Equal(new DateOnly(2026, 5, 8), request.DeliveryDate);
        Assert.Equal(routeId, request.RouteId);
        Assert.Equal(4, handler.SentRequests.Count);
        Assert.Contains($"accounts({accountId:D})", handler.SentRequests[1].Url);
        Assert.Contains($"mb_deliveryroutes({routeId:D})", handler.SentRequests[2].Url);
        Assert.Contains("mb_accountdeliveryoverrides", handler.SentRequests[3].Url);
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
        var routeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var driverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        var service = CreateService(handler);
        await service.UpdateOrderEffectiveDriverAsync(orderId, routeId, new EffectiveDriverResolutionResult(driverId, EffectiveDriverSource.RouteWeekday), TestContext.Current.CancellationToken);

        Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Patch, handler.SentRequests[0].Method);
        Assert.Contains($"mb_orders({orderId:D})", handler.SentRequests[0].Url);
        Assert.Contains($"\"mb_homedeliveryroute@odata.bind\":\"/mb_deliveryroutes({routeId:D})\"", handler.SentRequests[0].Body);
        Assert.Contains("\"mb_effectivedriversource\":\"RouteWeekday\"", handler.SentRequests[0].Body);
        Assert.Contains($"\"mb_effectivedriver@odata.bind\":\"/contacts({driverId:D})\"", handler.SentRequests[0].Body);
    }

    [Fact]
    public async Task UpdateOrderEffectiveDriver_ClearsDriverLookupWhenUncovered()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var routeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

        var service = CreateService(handler);
        await service.UpdateOrderEffectiveDriverAsync(orderId, routeId, new EffectiveDriverResolutionResult(null, EffectiveDriverSource.MissingRouteDriver), TestContext.Current.CancellationToken);

        Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Patch, handler.SentRequests[0].Method);
        Assert.Contains($"\"mb_homedeliveryroute@odata.bind\":\"/mb_deliveryroutes({routeId:D})\"", handler.SentRequests[0].Body);
        Assert.Contains("\"mb_effectivedriversource\":\"MissingRouteDriver\"", handler.SentRequests[0].Body);
        Assert.Contains("\"mb_effectivedriver@odata.bind\":null", handler.SentRequests[0].Body);
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
        Assert.Contains($"mb_order_kind eq {OrderKind.Sale}", decodedUrl);
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
        Assert.Contains($"\"mb_order_kind\":{OrderKind.Sale}", request.Body);
        Assert.Contains($"\"mb_Customer_account@odata.bind\":\"/accounts({accountId:D})\"", request.Body);
        Assert.Contains($"\"mb_Pricelist@odata.bind\":\"/mb_pricelists({priceListId:D})\"", request.Body);
    }
}
