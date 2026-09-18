using System.Text;
using System.Text.Json;
using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class ApplyCoverPlanTests
{
    private static readonly Guid DriverId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecondAccountId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateOnly FromDate = new(2026, 5, 8);
    private static readonly DateOnly ToDate = new(2026, 5, 10);

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly IEffectiveDriverRefresher _refresher = Substitute.For<IEffectiveDriverRefresher>();
    private readonly ApplyCoverPlan _function;

    public ApplyCoverPlanTests()
    {
        _dataverse.GetOverlappingAccountDeliveryOverridesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _function = new ApplyCoverPlan(_dataverse, _refresher, Substitute.For<ILogger<ApplyCoverPlan>>());
    }

    [Fact]
    public async Task RejectsOverlappingCoverWithoutWritingOrRefreshing()
    {
        _dataverse.GetOverlappingAccountDeliveryOverridesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), FromDate, ToDate, Arg.Any<CancellationToken>())
            .Returns([new AccountDeliveryOverrideRecord(AccountId, FromDate, ToDate, Guid.Parse("66666666-6666-6666-6666-666666666666"))]);

        var result = await _function.Run(CreateHttpRequest(ValidBody()), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("overlapping_cover", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("conflictingAccountCount").GetInt32());
        await _dataverse.DidNotReceive().CreateAccountDeliveryOverrideAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _refresher.DidNotReceive().RefreshAsync(Arg.Any<RefreshEffectiveDriversRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatesMissingOverridesSkipsIdenticalCoverAndRefreshesWithoutACap()
    {
        _dataverse.GetOverlappingAccountDeliveryOverridesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), FromDate, ToDate, Arg.Any<CancellationToken>())
            .Returns([new AccountDeliveryOverrideRecord(AccountId, FromDate, ToDate, DriverId)]);
        _dataverse.CreateAccountDeliveryOverrideAsync(SecondAccountId, DriverId, FromDate, ToDate, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _refresher.RefreshAsync(Arg.Any<RefreshEffectiveDriversRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefreshEffectiveDriversResponse("complete", 2, 2, 0, [], 1, 1));

        var body = ValidBody();
        body["accountIds"] = new[] { AccountId, SecondAccountId };
        var result = await _function.Run(CreateHttpRequest(body), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApplyCoverPlanResponse>(ok.Value);
        Assert.Equal("complete", response.Status);
        Assert.Equal(1, response.CreatedCount);
        Assert.Equal(1, response.AlreadyCoveredCount);
        Assert.Equal(2, response.EffectiveDriversRefreshedCount);
        Assert.Equal(1, response.DeliveryPacksRegeneratedCount);
        Assert.Equal(1, response.LockedPacksRequiringReviewCount);
        await _dataverse.Received(1).CreateAccountDeliveryOverrideAsync(
            SecondAccountId,
            DriverId,
            FromDate,
            ToDate,
            "Driver Rota - Bartek - North Shop",
            Arg.Any<CancellationToken>());
        await _dataverse.DidNotReceive().CreateAccountDeliveryOverrideAsync(AccountId, Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _refresher.Received(1).RefreshAsync(
            Arg.Is<RefreshEffectiveDriversRequest>(request =>
                request.MaxOrders == null &&
                request.FromDate == FromDate &&
                request.ToDate == ToDate &&
                request.AccountIds!.OrderBy(id => id).SequenceEqual(new[] { AccountId, SecondAccountId }.OrderBy(id => id))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepsCreatedOverridesWhenRefreshDoesNotCoverEveryOrder()
    {
        _dataverse.CreateAccountDeliveryOverrideAsync(AccountId, DriverId, FromDate, ToDate, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _refresher.RefreshAsync(Arg.Any<RefreshEffectiveDriversRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefreshEffectiveDriversResponse(
                "incomplete",
                1,
                0,
                0,
                [new RefreshEffectiveDriverOrderResult(Guid.Parse("11111111-1111-1111-1111-111111111111"), "failed", null, null, false, "Order was not found in Dataverse.")]));

        var result = await _function.Run(CreateHttpRequest(ValidBody()), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApplyCoverPlanResponse>(ok.Value);
        Assert.Equal("incomplete", response.Status);
        Assert.Equal(1, response.CreatedCount);
        Assert.Equal(0, response.EffectiveDriversRefreshedCount);
        Assert.Contains(response.Warnings, warning => warning.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(response.Warnings, warning => warning.Contains("every matching order", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object> ValidBody()
        => new()
        {
            ["driverId"] = DriverId,
            ["driverName"] = "Bartek",
            ["fromDate"] = FromDate,
            ["toDate"] = ToDate,
            ["accountIds"] = new[] { AccountId },
            ["accountNames"] = new Dictionary<string, string>
            {
                [AccountId.ToString("D")] = "Kept Shop",
                [SecondAccountId.ToString("D")] = "North Shop"
            }
        };

    private static HttpRequest CreateHttpRequest(object body)
    {
        var json = JsonSerializer.Serialize(body);
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        return context.Request;
    }
}
