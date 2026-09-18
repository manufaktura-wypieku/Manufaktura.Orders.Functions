using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class GenerateEmptyOrdersTests
{
    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly ILogger<GenerateEmptyOrders> _logger = Substitute.For<ILogger<GenerateEmptyOrders>>();
    private readonly ILogger<EmptyOrderGenerator> _generatorLogger = Substitute.For<ILogger<EmptyOrderGenerator>>();

    public GenerateEmptyOrdersTests()
    {
        _dataverse.ListActiveAccountsForDeliveryDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _dataverse.ListAccountIdsWithOrderOnDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
    }

    [Fact]
    public async Task SkipsTheExtraUtcTickThatIsNotTwoAmLondon()
    {
        var function = CreateFunction(new DateTimeOffset(2026, 1, 15, 1, 0, 0, TimeSpan.Zero));

        await function.Run(new TimerInfo { IsPastDue = false }, CancellationToken.None);

        await _dataverse.DidNotReceive().ListActiveAccountsForDeliveryDateAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunsAtTwoAmLondon()
    {
        var function = CreateFunction(new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero));

        await function.Run(new TimerInfo { IsPastDue = false }, CancellationToken.None);

        await _dataverse.Received(1).ListActiveAccountsForDeliveryDateAsync(new DateOnly(2026, 1, 15), Arg.Any<CancellationToken>());
        await _dataverse.Received(1).ListActiveAccountsForDeliveryDateAsync(new DateOnly(2026, 1, 16), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunsALatePastDueTick()
    {
        var function = CreateFunction(new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero));

        await function.Run(new TimerInfo { IsPastDue = true }, CancellationToken.None);

        await _dataverse.Received(1).ListActiveAccountsForDeliveryDateAsync(new DateOnly(2026, 7, 15), Arg.Any<CancellationToken>());
    }

    private GenerateEmptyOrders CreateFunction(DateTimeOffset utcNow)
        => new(_dataverse, new FixedTimeProvider(utcNow), _logger, _generatorLogger);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
