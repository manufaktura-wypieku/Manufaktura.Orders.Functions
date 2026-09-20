using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class DeliveryNoteDocumentGeneratorTests
{
    private static readonly Guid NoteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly ISharePointService _sharePoint = Substitute.For<ISharePointService>();
    private readonly IDeliveryNoteWordTemplateFiller _filler = Substitute.For<IDeliveryNoteWordTemplateFiller>();
    private readonly ILogger<DeliveryNoteDocumentGenerator> _logger = Substitute.For<ILogger<DeliveryNoteDocumentGenerator>>();
    private readonly DeliveryNoteDocumentGenerator _generator;

    public DeliveryNoteDocumentGeneratorTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DeliveryNoteTemplatePath"] = "Templates/delivery_note_patterns_footer.docx"
        }).Build();

        _generator = new DeliveryNoteDocumentGenerator(_dataverse, _sharePoint, _filler, config, _logger);
    }

    [Fact]
    public async Task SkipsWhenUrlExistsAndForceIsFalse()
    {
        _dataverse.GetDeliveryNoteDocumentSourceAsync(NoteId, Arg.Any<CancellationToken>())
            .Returns(CreateSource(existingUrl: "https://example/existing.pdf"));

        var result = await _generator.GenerateAsync(NoteId, force: false, CancellationToken.None);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("already_has_url", result.Reason);
        await _sharePoint.DidNotReceive().DownloadFileByPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThrowsWhenLinePricesMissing()
    {
        var source = CreateSource(existingUrl: null) with
        {
            Lines =
            [
                new DeliveryNoteLine("Chleb", "Bread", 2, null, null, "10")
            ]
        };
        _dataverse.GetDeliveryNoteDocumentSourceAsync(NoteId, Arg.Any<CancellationToken>()).Returns(source);

        await Assert.ThrowsAsync<OrderItemsNotReadyException>(() =>
            _generator.GenerateAsync(NoteId, force: false, CancellationToken.None));
    }

    [Fact]
    public async Task UploadsPdfDeletesDocxAndUpdatesNote()
    {
        var source = CreateSource(existingUrl: null);
        _dataverse.GetDeliveryNoteDocumentSourceAsync(NoteId, Arg.Any<CancellationToken>()).Returns(source);
        _sharePoint.DownloadFileByPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
        _filler.Fill(Arg.Any<byte[]>(), source).Returns([4, 5, 6]);
        _sharePoint.DownloadAsPdfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([7, 8, 9]);
        _sharePoint.UploadFileAsync(
                DeliveryNoteDocumentGenerator.DeliveryNotesFolder,
                Arg.Is<string>(n => n.EndsWith(".pdf")),
                Arg.Any<byte[]>(),
                "application/pdf",
                Arg.Any<CancellationToken>())
            .Returns("https://example/Delivery%20notes/note.pdf");

        var result = await _generator.GenerateAsync(NoteId, force: false, CancellationToken.None);

        Assert.Equal("complete", result.Status);
        Assert.Equal("https://example/Delivery%20notes/note.pdf", result.Url);
        Assert.Equal("DN-ORD-100", result.Name);
        await _sharePoint.Received(1).DeleteFileByPathAsync(Arg.Is<string>(p => p.EndsWith(".docx")), Arg.Any<CancellationToken>());
        await _dataverse.Received(1).UpdateDeliveryNoteDocumentAsync(NoteId, "DN-ORD-100", "https://example/Delivery%20notes/note.pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildFileStemUsesNoteCreatedOn()
    {
        var source = CreateSource(null);
        var stem = DeliveryNoteDocumentGenerator.BuildFileStem(source);
        Assert.StartsWith("DN-A1-ORD-100-", stem);
        Assert.Contains("2026-09-20T10-00-00Z", stem);
    }

    private static DeliveryNoteDocumentSource CreateSource(string? existingUrl) =>
        new(
            NoteId,
            new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero),
            existingUrl,
            "ORD-100",
            new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
            12.50m,
            new DeliveryNoteCustomer("Shop", "A1", "Street 1", "00-001", "Warsaw", false),
            [
                new DeliveryNoteLine("Chleb", "Bread", 2, 1.25m, 2.50m, "10")
            ]);
}
