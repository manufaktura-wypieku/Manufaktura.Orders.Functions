using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
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
    public async Task ReturnssPdfWhenDocumentsProvided()
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

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ReturnsBadRequestWhenBodyIsNull()
    {
        var request = CreateHttpRequest<object?>(null);
        var result = await _function.Run(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
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
}
