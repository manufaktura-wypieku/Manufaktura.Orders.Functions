using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class GenerateDeliveryNoteDocumentTests
{
    private static readonly Guid NoteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly IDeliveryNoteDocumentGenerator _generator = Substitute.For<IDeliveryNoteDocumentGenerator>();
    private readonly ILogger<GenerateDeliveryNoteDocument> _logger = Substitute.For<ILogger<GenerateDeliveryNoteDocument>>();
    private readonly GenerateDeliveryNoteDocument _function;

    public GenerateDeliveryNoteDocumentTests()
    {
        _function = new GenerateDeliveryNoteDocument(_generator, _logger);
    }

    [Fact]
    public async Task ReturnsBadRequestWhenDeliveryNoteIdMissing()
    {
        var result = await _function.Run(CreateHttpRequest(new { deliveryNoteId = Guid.Empty }), CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertCode(bad.Value, "missing_delivery_note_id");
    }

    [Fact]
    public async Task ReturnsBadRequestOnInvalidJson()
    {
        var result = await _function.Run(CreateHttpRequest("{not-json"), CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertCode(bad.Value, "invalid_json");
    }

    [Fact]
    public async Task ReturnsSkippedWhenGeneratorSkips()
    {
        _generator.GenerateAsync(NoteId, false, Arg.Any<CancellationToken>())
            .Returns(new DeliveryNoteDocumentGenerationResult("skipped", "already_has_url", NoteId, "https://example/doc.pdf"));

        var result = await _function.Run(CreateHttpRequest(new { deliveryNoteId = NoteId }), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("skipped", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("already_has_url", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReturnsCompleteWhenGeneratorSucceeds()
    {
        _generator.GenerateAsync(NoteId, true, Arg.Any<CancellationToken>())
            .Returns(new DeliveryNoteDocumentGenerationResult("complete", null, NoteId, "https://example/doc.pdf", "DN-ORD-1"));

        var result = await _function.Run(CreateHttpRequest(new { deliveryNoteId = NoteId, force = true }), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("complete", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("https://example/doc.pdf", doc.RootElement.GetProperty("url").GetString());
        await _generator.Received(1).GenerateAsync(NoteId, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnsUnprocessableWhenOrderItemsNotReady()
    {
        _generator.GenerateAsync(NoteId, false, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OrderItemsNotReadyException(NoteId));

        var result = await _function.Run(CreateHttpRequest(new { deliveryNoteId = NoteId }), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, obj.StatusCode);
        AssertCode(obj.Value, "order_items_not_ready");
    }

    [Fact]
    public async Task ReturnsNotFoundWhenUpstreamReturns404()
    {
        _generator.GenerateAsync(NoteId, false, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("missing", null, HttpStatusCode.NotFound));

        var result = await _function.Run(CreateHttpRequest(new { deliveryNoteId = NoteId }), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        AssertCode(notFound.Value, "delivery_note_not_found");
    }

    private static HttpRequest CreateHttpRequest(object body)
    {
        var json = body is string s ? s : JsonSerializer.Serialize(body);
        return CreateHttpRequest(json);
    }

    private static HttpRequest CreateHttpRequest(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentType = "application/json";
        return context.Request;
    }

    private static void AssertCode(object? value, string expectedCode)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
    }
}
