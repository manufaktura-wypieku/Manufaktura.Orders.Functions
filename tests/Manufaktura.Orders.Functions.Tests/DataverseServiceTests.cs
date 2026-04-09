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
        var (packId, created) = await service.CreateDeliveryPackAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 5);

        Assert.True(created);
        Assert.Equal(newPackId, packId);
    }

    [Fact]
    public async Task CreateDeliveryPack_Returns409Conflict_IsCreatedFalse()
    {
        var existingPackId = Guid.Parse("bbbbbbbb-0002-0002-0002-000000000002");

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Conflict)); // POST → 409
        handler.Enqueue(OkJson($$"""{"value":[{"mb_deliverypackid":"{{existingPackId:D}}","mb_statusreason":1}]}""")); // GET re-query → 200

        var service = CreateService(handler);
        var (packId, created) = await service.CreateDeliveryPackAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, 5);

        Assert.False(created);
        Assert.Equal(existingPackId, packId);
        Assert.Equal(2, handler.SentRequests.Count);
        Assert.Equal(HttpMethod.Post, handler.SentRequests[0].Method);
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
}
