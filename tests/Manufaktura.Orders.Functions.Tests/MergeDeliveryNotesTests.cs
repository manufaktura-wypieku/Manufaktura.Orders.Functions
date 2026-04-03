using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text;
using System.Text.Json;

namespace Manufaktura.Orders.Functions.Tests;

public class MergeDeliveryNotesTests
{
    private readonly IDocumentMergeService _mergeService = Substitute.For<IDocumentMergeService>();
    private readonly ILogger<MergeDeliveryNotes> _logger = Substitute.For<ILogger<MergeDeliveryNotes>>();
    private readonly MergeDeliveryNotes _function;

    public MergeDeliveryNotesTests()
    {
        _function = new MergeDeliveryNotes(_mergeService, _logger);
    }

    [Fact]
    public async Task ReturnsPdfWhenDocumentsProvided()
    {
        var urls = new[] { "https://example.sharepoint.com/sites/Site/Shared%20Documents/doc1.docx" };
        var expectedPdf = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF header

        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Returns(expectedPdf);

        var request = CreateHttpRequest(new { documentUrls = urls });
        var result = await _function.Run(request, CancellationToken.None);

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
        Assert.Equal("DeliveryPack.pdf", fileResult.FileDownloadName);
        Assert.Equal(expectedPdf, fileResult.FileContents);
    }

    [Fact]
    public async Task ReturnsBadRequestWhenNoUrls()
    {
        var request = CreateHttpRequest(new { documentUrls = Array.Empty<string>() });
        var result = await _function.Run(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
        Assert.Equal("missing_document_urls", payload.RootElement.GetProperty("code").GetString());
        Assert.Equal("documentUrls array is required and must not be empty.", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReturnsBadRequestWhenBodyIsEmpty()
    {
        var request = CreateEmptyHttpRequest();
        var result = await _function.Run(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ReturnsBadRequestWhenBodyIsJsonNull()
    {
        var request = CreateHttpRequest<object?>(null);
        var result = await _function.Run(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ReturnsBadRequestWhenBodyIsInvalidJson()
    {
        var request = CreateRawHttpRequest("{ not valid json }");
        var result = await _function.Run(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(badRequest.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_json", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReturnsBadRequestWhenDocumentUrlsPropertyIsMissing()
    {
        var request = CreateRawHttpRequest("{}");
        var result = await _function.Run(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(badRequest.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("missing_document_urls", doc.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task ReturnsBadRequestWhenMergeServiceThrowsArgumentException()
    {
        var urls = new[] { "not-a-sharepoint-url" };

        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Throws(new ArgumentException("Invalid SharePoint URL format."));

        var request = CreateHttpRequest(new { documentUrls = urls });
        var result = await _function.Run(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(badRequest.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_document_urls", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReturnsBadGatewayWhenMergeServiceThrowsHttpRequestException()
    {
        var urls = new[] { "https://example.sharepoint.com/sites/Site/Shared%20Documents/doc1.docx" };

        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .Throws(new System.Net.Http.HttpRequestException("Graph API unreachable."));

        var request = CreateHttpRequest(new { documentUrls = urls });
        var result = await _function.Run(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var json = JsonSerializer.Serialize(objectResult.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("document_fetch_failed", doc.RootElement.GetProperty("code").GetString());
    }

    private static HttpRequest CreateEmptyHttpRequest()
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Body = new MemoryStream();
        request.ContentType = "application/json";
        return request;
    }

    private static HttpRequest CreateHttpRequest<T>(T body)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        var json = JsonSerializer.Serialize(body);
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        request.ContentType = "application/json";
        return request;
    }

    private static HttpRequest CreateRawHttpRequest(string rawBody)
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
        request.ContentType = "application/json";
        return request;
    }
}
