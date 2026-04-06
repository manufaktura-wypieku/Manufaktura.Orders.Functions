using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class GenerateDeliveryPackTests
{
    private static readonly Guid NoteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RouteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PackId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset DeliveryDate = new(2026, 4, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly IDocumentMergeService _mergeService = Substitute.For<IDocumentMergeService>();
    private readonly ISharePointService _sharePoint = Substitute.For<ISharePointService>();
    private readonly ILogger<GenerateDeliveryPack> _logger = Substitute.For<ILogger<GenerateDeliveryPack>>();
    private readonly GenerateDeliveryPack _function;

    public GenerateDeliveryPackTests()
    {
        _function = new GenerateDeliveryPack(_dataverse, _mergeService, _sharePoint, _logger);
    }

    [Fact]
    public async Task ReturnsBadRequestWhenBodyIsNull()
    {
        var request = CreateHttpRequest("null");
        var result = await _function.Run(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertErrorCode(bad.Value, "missing_delivery_note_id");
    }

    [Fact]
    public async Task ReturnsBadRequestWhenDeliveryNoteIdIsEmpty()
    {
        var request = CreateHttpRequest(new { deliveryNoteId = Guid.Empty });
        var result = await _function.Run(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertErrorCode(bad.Value, "missing_delivery_note_id");
    }

    [Fact]
    public async Task ReturnsBadRequestOnInvalidJson()
    {
        var request = CreateHttpRequest("{invalid-json");
        var result = await _function.Run(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertErrorCode(bad.Value, "invalid_json");
    }

    [Fact]
    public async Task ReturnsSkippedWhenOrderCountIsZero()
    {
        SetupNote();
        SetupCounts(orderCount: 0, noteCount: 0);

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        AssertStatus(ok.Value, "skipped");
        AssertCode(ok.Value, "no_orders");
        await _dataverse.DidNotReceive().CountDeliveryNotesWithUrlAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _dataverse.DidNotReceive().GetDeliveryPackAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnsSkippedWhenDocumentUrlsIsEmpty()
    {
        SetupNote();
        SetupCounts(orderCount: 2, noteCount: 2);
        _dataverse.GetDeliveryPackAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns((DeliveryPackRecord?)null);
        _dataverse.GetDeliveryNoteUrlsAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns([]);

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        AssertStatus(ok.Value, "skipped");
        AssertCode(ok.Value, "no_document_urls");
        await _dataverse.DidNotReceive().CreateDeliveryPackAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnsSkippedWhenNotAllNotesAreReady()
    {
        SetupNote();
        _dataverse.CountCompletedOrdersByRouteAndDateAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns(3);
        _dataverse.CountDeliveryNotesWithUrlAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns(2); // 2 < 3

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        AssertStatus(ok.Value, "skipped");
        AssertCode(ok.Value, "not_all_notes_ready");
    }

    [Fact]
    public async Task ReturnsSkippedWhenPackIsAlreadyGenerating()
    {
        SetupNote();
        SetupCounts(orderCount: 2, noteCount: 2);
        _dataverse.GetDeliveryPackAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>())
            .Returns(new DeliveryPackRecord(PackId, DeliveryPackStatus.Generating));

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        AssertStatus(ok.Value, "skipped");
        AssertCode(ok.Value, "already_generating");
    }

    [Fact]
    public async Task CreatesNewPackAndReturnsCompleteWhenNoExistingPack()
    {
        SetupNote();
        SetupCounts(orderCount: 2, noteCount: 2);
        _dataverse.GetDeliveryPackAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns((DeliveryPackRecord?)null);
        _dataverse.CreateDeliveryPackAsync(RouteId, DeliveryDate, 2, Arg.Any<CancellationToken>()).Returns(PackId);
        _dataverse.GetDeliveryNoteUrlsAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>())
            .Returns(["https://sp.example.com/sites/Dev/Shared%20Documents/note1.pdf",
                      "https://sp.example.com/sites/Dev/Shared%20Documents/note2.pdf"]);
        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>()).Returns([0x25, 0x50, 0x44, 0x46]);
        _dataverse.GetDeliveryRouteNameAsync(RouteId, Arg.Any<CancellationToken>()).Returns("Route-A");
        _sharePoint.UploadDeliveryPackAsync("Route-A", DeliveryDate, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns("https://sp.example.com/sites/Dev/Shared%20Documents/DeliveryPacks/Route-A/2026-04-07-delivery-pack.pdf");

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        AssertStatus(ok.Value, "complete");

        await _dataverse.Received(1).CreateDeliveryPackAsync(RouteId, DeliveryDate, 2, Arg.Any<CancellationToken>());
        await _dataverse.Received(1).UpdateDeliveryPackCompleteAsync(PackId, Arg.Any<string>(), 2, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatesExistingCompletePackAndReturnsComplete()
    {
        SetupNote();
        SetupCounts(orderCount: 2, noteCount: 2);
        _dataverse.GetDeliveryPackAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>())
            .Returns(new DeliveryPackRecord(PackId, DeliveryPackStatus.Complete)); // Re-trigger for regeneration
        _dataverse.GetDeliveryNoteUrlsAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>())
            .Returns(["https://sp.example.com/sites/Dev/Shared%20Documents/note1.pdf",
                      "https://sp.example.com/sites/Dev/Shared%20Documents/note2.pdf"]);
        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>()).Returns([0x25, 0x50, 0x44, 0x46]);
        _dataverse.GetDeliveryRouteNameAsync(RouteId, Arg.Any<CancellationToken>()).Returns("Route-A");
        _sharePoint.UploadDeliveryPackAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns("https://sp.example.com/DeliveryPacks/Route-A/2026-04-07-delivery-pack.pdf");

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        AssertStatus(ok.Value, "complete");

        await _dataverse.Received(1).SetDeliveryPackGeneratingAsync(PackId, 2, Arg.Any<CancellationToken>());
        await _dataverse.DidNotReceive().CreateDeliveryPackAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarksPackFailedAndReturns500WhenMergeFails()
    {
        SetupNote();
        SetupCounts(orderCount: 1, noteCount: 1);
        _dataverse.GetDeliveryPackAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns((DeliveryPackRecord?)null);
        _dataverse.CreateDeliveryPackAsync(RouteId, DeliveryDate, 1, Arg.Any<CancellationToken>()).Returns(PackId);
        _dataverse.GetDeliveryNoteUrlsAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>())
            .Returns(["https://sp.example.com/sites/Dev/Shared%20Documents/note1.pdf"]);
        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Download failed"));

        var request = CreateHttpRequest(new { deliveryNoteId = NoteId });
        var result = await _function.Run(request, CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);
        await _dataverse.Received(1).UpdateDeliveryPackFailedAsync(PackId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void SetupNote() =>
        _dataverse.GetDeliveryNoteAsync(NoteId, Arg.Any<CancellationToken>())
            .Returns(new DeliveryNoteRecord(NoteId, RouteId, DeliveryDate));

    private void SetupCounts(int orderCount, int noteCount)
    {
        _dataverse.CountCompletedOrdersByRouteAndDateAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns(orderCount);
        _dataverse.CountDeliveryNotesWithUrlAsync(RouteId, DeliveryDate, Arg.Any<CancellationToken>()).Returns(noteCount);
    }

    private static HttpRequest CreateHttpRequest(object body)
    {
        var json = JsonSerializer.Serialize(body);
        return CreateHttpRequest(json);
    }

    private static HttpRequest CreateHttpRequest(string json)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        return context.Request;
    }

    private static void AssertErrorCode(object? value, string expectedCode)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
    }

    private static void AssertStatus(object? value, string expectedStatus)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.Equal(expectedStatus, doc.RootElement.GetProperty("status").GetString());
    }

    private static void AssertCode(object? value, string expectedReason)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.Equal(expectedReason, doc.RootElement.GetProperty("reason").GetString());
    }
}
