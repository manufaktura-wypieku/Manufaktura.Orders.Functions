using System.Net;
using System.Text;
using System.Text.Json;
using Manufaktura.Orders.Functions.Functions;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Manufaktura.Orders.Functions.Tests;

public class RefreshEffectiveDriversTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondOrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RouteId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DriverId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateOnly DeliveryDate = new(2026, 5, 8);

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly IEffectiveDriverResolver _resolver = Substitute.For<IEffectiveDriverResolver>();
    private readonly RefreshEffectiveDrivers _function;

    public RefreshEffectiveDriversTests()
    {
        _function = new RefreshEffectiveDrivers(_dataverse, _resolver, Substitute.For<ILogger<RefreshEffectiveDrivers>>());
    }

    [Fact]
    public async Task ReturnsBadRequestWhenMaxOrdersIsInvalid()
    {
        var request = CreateHttpRequest(new { maxOrders = 0 });

        var result = await _function.Run(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertErrorCode(bad.Value, "invalid_max_orders");
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
    public async Task RefreshesExplicitOrderAndPersistsResolution()
    {
        var resolutionRequest = CreateResolutionRequest();
        var resolution = new EffectiveDriverResolutionResult(DriverId, EffectiveDriverSource.RouteWeekday);
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>()).Returns(resolutionRequest);
        _resolver.Resolve(resolutionRequest).Returns(resolution);
        _dataverse.UpdateOrderEffectiveDriverAsync(OrderId, resolution, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var request = CreateHttpRequest(new { orderId = OrderId });

        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RefreshEffectiveDriversResponse>(ok.Value);
        Assert.Equal("complete", response.Status);
        Assert.Equal(1, response.MatchedOrders);
        Assert.Equal(1, response.UpdatedOrders);
        Assert.Equal(0, response.UncoveredOrders);

        var orderResult = Assert.Single(response.Results);
        Assert.Equal(OrderId, orderResult.OrderId);
        Assert.Equal("updated", orderResult.Status);
        Assert.Equal(DriverId, orderResult.DriverId);
        Assert.Equal(nameof(EffectiveDriverSource.RouteWeekday), orderResult.Source);
        await _dataverse.DidNotReceive().GetOrderIdsForEffectiveDriverRefreshAsync(Arg.Any<EffectiveDriverRefreshQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshesFilteredOrderSetAndCountsUncoveredOrders()
    {
        var firstRequest = CreateResolutionRequest();
        var secondRequest = CreateResolutionRequest();
        var firstResolution = new EffectiveDriverResolutionResult(DriverId, EffectiveDriverSource.RouteWeekday);
        var secondResolution = new EffectiveDriverResolutionResult(null, EffectiveDriverSource.UncoveredDueToDriverAbsence);

        _dataverse.GetOrderIdsForEffectiveDriverRefreshAsync(Arg.Any<EffectiveDriverRefreshQuery>(), Arg.Any<CancellationToken>())
            .Returns([OrderId, SecondOrderId]);
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>()).Returns(firstRequest);
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(SecondOrderId, Arg.Any<CancellationToken>()).Returns(secondRequest);
        _resolver.Resolve(firstRequest).Returns(firstResolution);
        _resolver.Resolve(secondRequest).Returns(secondResolution);
        _dataverse.UpdateOrderEffectiveDriverAsync(OrderId, firstResolution, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _dataverse.UpdateOrderEffectiveDriverAsync(SecondOrderId, secondResolution, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var request = CreateHttpRequest(new
        {
            routeId = RouteId,
            fromDate = DeliveryDate,
            toDate = DeliveryDate.AddDays(3),
            maxOrders = 10
        });

        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RefreshEffectiveDriversResponse>(ok.Value);
        Assert.Equal(2, response.MatchedOrders);
        Assert.Equal(2, response.UpdatedOrders);
        Assert.Equal(1, response.UncoveredOrders);
        Assert.Contains(response.Results, item => item.OrderId == SecondOrderId && item.IsUncovered);

        await _dataverse.Received(1).GetOrderIdsForEffectiveDriverRefreshAsync(
            Arg.Is<EffectiveDriverRefreshQuery>(query =>
                query.RouteId == RouteId &&
                query.FromDate == DeliveryDate &&
                query.ToDate == DeliveryDate.AddDays(3) &&
                query.MaxOrders == 10),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepsRefreshingWhenOneOrderHasInvalidContext()
    {
        _dataverse.GetOrderIdsForEffectiveDriverRefreshAsync(Arg.Any<EffectiveDriverRefreshQuery>(), Arg.Any<CancellationToken>())
            .Returns([OrderId]);
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Order has no delivery route assigned."));

        var request = CreateHttpRequest(new { fromDate = DeliveryDate });

        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<RefreshEffectiveDriversResponse>(ok.Value);
        Assert.Equal(1, response.MatchedOrders);
        Assert.Equal(0, response.UpdatedOrders);

        var orderResult = Assert.Single(response.Results);
        Assert.Equal("failed", orderResult.Status);
        Assert.Contains("no delivery route", orderResult.Error);
        await _dataverse.DidNotReceive().UpdateOrderEffectiveDriverAsync(Arg.Any<Guid>(), Arg.Any<EffectiveDriverResolutionResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnsBadGatewayOnUpstreamAuthFailure()
    {
        _dataverse.GetOrderIdsForEffectiveDriverRefreshAsync(Arg.Any<EffectiveDriverRefreshQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Denied", null, HttpStatusCode.Forbidden));

        var request = CreateHttpRequest(new { fromDate = DeliveryDate });

        var result = await _function.Run(request, CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
        AssertErrorCode(error.Value, "upstream_auth_failure");
    }

    private static EffectiveDriverResolutionRequest CreateResolutionRequest()
        => new(
            AccountId,
            DeliveryDate,
            new RouteDriverSchedule(DriverId, new Dictionary<DayOfWeek, Guid?>()),
            [],
            []);

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
}
