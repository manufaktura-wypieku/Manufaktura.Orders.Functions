using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class EmptyOrderGeneratorTests
{
    private static readonly Guid BakeryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CafeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PriceListId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateOnly Today = new(2026, 9, 17);

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly EmptyOrderGenerator _generator;

    public EmptyOrderGeneratorTests()
    {
        _generator = new EmptyOrderGenerator(_dataverse, Substitute.For<ILogger>());
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _dataverse.ListAccountIdsWithOrderOnDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
    }

    [Fact]
    public async Task CreatesTodaysMissingOrdersBeforeTomorrows()
    {
        var bakery = new EmptyOrderAccount(BakeryId, "The Bakery", PriceListId);
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Today, Arg.Any<CancellationToken>())
            .Returns([bakery]);
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Today.AddDays(1), Arg.Any<CancellationToken>())
            .Returns([bakery]);

        await _generator.GenerateAsync(Today);

        Received.InOrder(() =>
        {
            _dataverse.CreateEmptyOrderAsync(BakeryId, PriceListId, Today, Arg.Any<CancellationToken>());
            _dataverse.CreateEmptyOrderAsync(BakeryId, PriceListId, Today.AddDays(1), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task SkipsAccountThatAlreadyHasAnOrder()
    {
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Today, Arg.Any<CancellationToken>())
            .Returns([new EmptyOrderAccount(BakeryId, "The Bakery", PriceListId)]);
        _dataverse.ListAccountIdsWithOrderOnDateAsync(Today, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { BakeryId });

        await _generator.GenerateAsync(Today);

        await _dataverse.DidNotReceive().CreateEmptyOrderAsync(BakeryId, PriceListId, Today, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipsAccountWithNoPriceList_AndStillCreatesTheNextAccount()
    {
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Today, Arg.Any<CancellationToken>())
            .Returns(
            [
                new EmptyOrderAccount(BakeryId, "The Bakery", null),
                new EmptyOrderAccount(CafeId, "The Cafe", PriceListId)
            ]);

        await _generator.GenerateAsync(Today);

        await _dataverse.DidNotReceive().CreateEmptyOrderAsync(
            BakeryId, Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _dataverse.Received(1).CreateEmptyOrderAsync(CafeId, PriceListId, Today, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContinuesAfterACreateFailure_ThenFailsTheRun()
    {
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Today, Arg.Any<CancellationToken>())
            .Returns(
            [
                new EmptyOrderAccount(BakeryId, "The Bakery", PriceListId),
                new EmptyOrderAccount(CafeId, "The Cafe", PriceListId)
            ]);
        _dataverse.CreateEmptyOrderAsync(BakeryId, PriceListId, Today, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Dataverse rejected the create."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _generator.GenerateAsync(Today));

        Assert.Contains("1 account", ex.Message);
        await _dataverse.Received(1).CreateEmptyOrderAsync(CafeId, PriceListId, Today, Arg.Any<CancellationToken>());
    }
}
