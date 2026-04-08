using System.Net;
using System.Text;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class SharePointServiceTests
{
    private const string SiteUrl = "https://manufakturawypieku.sharepoint.com/sites/Manufaktura-DEV";
    private const string SiteGraphUrlBase = "https://graph.microsoft.com/v1.0/sites/manufakturawypieku.sharepoint.com:/sites/Manufaktura-DEV:";

    private static SharePointService CreateService(FakeHttpMessageHandler handler)
    {
        var config = Substitute.For<IConfiguration>();
        config["SharePointSiteUrl"].Returns(SiteUrl);
        return new SharePointService(new HttpClient(handler), new FakeTokenCredential(), config);
    }

    private static HttpResponseMessage OkJson(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---

    [Fact]
    public async Task SmallFile_SendsTwoFolderCreatesAndSimplePut_AndReturnsSharePointUrl()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // POST DeliveryPacks folder
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // POST route folder
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // PUT content (simple upload)

        var service = CreateService(handler);
        var pdf = new byte[100];
        var returnedUrl = await service.UploadDeliveryPackAsync("Route A", new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero), pdf);

        Assert.Equal(3, handler.SentRequests.Count);

        // Folder create 1: root/children for DeliveryPacks
        var req0 = handler.SentRequests[0];
        Assert.Equal(HttpMethod.Post, req0.Method);
        Assert.Equal($"{SiteGraphUrlBase}/drive/root/children", req0.Url);
        Assert.Contains("\"DeliveryPacks\"", req0.Body);
        Assert.Contains("\"fail\"", req0.Body);

        // Folder create 2: root:/DeliveryPacks:/children for route sub-folder
        var req1 = handler.SentRequests[1];
        Assert.Equal(HttpMethod.Post, req1.Method);
        Assert.Equal($"{SiteGraphUrlBase}/drive/root:/DeliveryPacks:/children", req1.Url);
        Assert.Contains("\"Route A\"", req1.Body);

        // Simple PUT to upload content
        var req2 = handler.SentRequests[2];
        Assert.Equal(HttpMethod.Put, req2.Method);
        Assert.EndsWith(":/content", req2.Url);

        Assert.Equal(
            "https://manufakturawypieku.sharepoint.com/sites/Manufaktura-DEV/Shared%20Documents/DeliveryPacks/Route%20A/2026-04-06-delivery-pack.pdf",
            returnedUrl);
    }

    [Fact]
    public async Task LargeFile_CreatesUploadSessionAndSendsChunksWithCorrectContentRange()
    {
        // 4,100,000 bytes > 4,000,000 threshold → upload session path
        // Chunk size = 3,276,800 → two chunks: [0-3276799] + [3276800-4099999]
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // POST DeliveryPacks folder
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // POST route folder
        handler.Enqueue(OkJson("""{"uploadUrl":"https://upload.example.com/session"}""")); // createUploadSession
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted) // chunk 1 → 202
        {
            Content = new StringContent("""{"nextExpectedRanges":["3276800-4099999"]}""", Encoding.UTF8, "application/json")
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // chunk 2 → 201 (complete)

        var service = CreateService(handler);
        var pdf = new byte[4_100_000];
        await service.UploadDeliveryPackAsync("Route A", new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero), pdf);

        Assert.Equal(5, handler.SentRequests.Count);

        var sessionReq = handler.SentRequests[2];
        Assert.Equal(HttpMethod.Post, sessionReq.Method);
        Assert.Contains("createUploadSession", sessionReq.Url);

        var chunk1 = handler.SentRequests[3];
        Assert.Equal(HttpMethod.Put, chunk1.Method);
        Assert.Equal("https://upload.example.com/session", chunk1.Url);
        Assert.Equal("bytes 0-3276799/4100000", chunk1.ContentRange);

        var chunk2 = handler.SentRequests[4];
        Assert.Equal(HttpMethod.Put, chunk2.Method);
        Assert.Equal("https://upload.example.com/session", chunk2.Url);
        Assert.Equal("bytes 3276800-4099999/4100000", chunk2.ContentRange);
    }
}
