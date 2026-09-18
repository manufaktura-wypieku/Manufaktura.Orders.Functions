using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class DeliveryPackRegeneratorTests
{
    private static readonly Guid PreviousDriverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NewDriverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActivePackId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NewPackId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LockedPackId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateOnly DeliveryDate = new(2026, 5, 8);
    private static readonly DateTimeOffset DeliveryTimestamp = new(2026, 5, 8, 0, 0, 0, TimeSpan.Zero);

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly IDocumentMergeService _mergeService = Substitute.For<IDocumentMergeService>();
    private readonly ISharePointService _sharePoint = Substitute.For<ISharePointService>();
    private readonly DeliveryPackRegenerator _regenerator;

    public DeliveryPackRegeneratorTests()
    {
        _regenerator = new DeliveryPackRegenerator(
            _dataverse,
            _mergeService,
            _sharePoint,
            Substitute.For<ILogger<DeliveryPackRegenerator>>());
    }

    [Fact]
    public async Task DoesNothingWhenTheEffectiveDriverDidNotChange()
    {
        var result = await _regenerator.RegenerateAsync(PreviousDriverId, PreviousDriverId, DeliveryDate, CancellationToken.None);

        Assert.Equal(0, result.RegeneratedPacks);
        await _dataverse.DidNotReceive().CountTotalDeliveryNotesForDriverAndDateAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletesTheOldEmptyPackAndCreatesTheNewPackMarkedAgainstLockedHistory()
    {
        _dataverse.CountTotalDeliveryNotesForDriverAndDateAsync(PreviousDriverId, DeliveryTimestamp, Arg.Any<CancellationToken>()).Returns(0);
        _dataverse.GetActiveDeliveryPackAsync(PreviousDriverId, DeliveryTimestamp, Arg.Any<CancellationToken>())
            .Returns(new DeliveryPackRecord(ActivePackId, DeliveryPackStatus.Complete));
        _dataverse.CountTotalDeliveryNotesForDriverAndDateAsync(NewDriverId, DeliveryTimestamp, Arg.Any<CancellationToken>()).Returns(1);
        _dataverse.CountDeliveryNotesWithUrlForDriverAndDateAsync(NewDriverId, DeliveryTimestamp, Arg.Any<CancellationToken>()).Returns(1);
        _dataverse.GetDeliveryNoteUrlsForDriverAndDateAsync(NewDriverId, DeliveryTimestamp, Arg.Any<CancellationToken>())
            .Returns(["https://sp.example/note.pdf"]);
        _dataverse.GetDriverNameAsync(NewDriverId, Arg.Any<CancellationToken>()).Returns("Bartek");
        _dataverse.CreateDeliveryPackAsync(NewDriverId, DeliveryTimestamp, 1, "Bartek - 2026-05-08", Arg.Any<CancellationToken>())
            .Returns((NewPackId, true));
        _dataverse.GetLockedDeliveryPackIdsAsync(NewDriverId, DeliveryTimestamp, Arg.Any<CancellationToken>())
            .Returns([LockedPackId]);
        _mergeService.MergeDocumentsAsync(Arg.Any<string[]>(), Arg.Any<CancellationToken>()).Returns([0x25, 0x50, 0x44, 0x46]);
        _sharePoint.UploadDeliveryPackAsync("Bartek", DeliveryTimestamp, Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns("https://sp.example/pack.pdf");

        var result = await _regenerator.RegenerateAsync(PreviousDriverId, NewDriverId, DeliveryDate, CancellationToken.None);

        Assert.Equal(2, result.RegeneratedPacks);
        Assert.Equal(1, result.LockedPacksRequiringReview);
        Assert.Empty(result.Warnings);
        await _dataverse.Received(1).DeleteDeliveryPackAsync(ActivePackId, Arg.Any<CancellationToken>());
        await _dataverse.Received(1).AppendDeliveryPackLogAsync(LockedPackId, $"Superseded by delivery pack {NewPackId:D}.", Arg.Any<CancellationToken>());
        await _dataverse.Received(1).UpdateDeliveryPackCompleteAsync(NewPackId, "https://sp.example/pack.pdf", 1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}
