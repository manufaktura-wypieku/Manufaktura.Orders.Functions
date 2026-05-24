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

public class ResolveEffectiveDriverTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RouteId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DriverId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateOnly DeliveryDate = new(2026, 5, 8);

    private readonly IDataverseService _dataverse = Substitute.For<IDataverseService>();
    private readonly IEffectiveDriverResolver _resolver = Substitute.For<IEffectiveDriverResolver>();
    private readonly ResolveEffectiveDriver _function;

    public ResolveEffectiveDriverTests()
    {
        _function = new ResolveEffectiveDriver(_dataverse, _resolver, Substitute.For<ILogger<ResolveEffectiveDriver>>());
    }

    [Fact]
    public async Task ReturnsBadRequestWhenOrderIdIsEmpty()
    {
        var request = CreateHttpRequest(new { orderId = Guid.Empty });

        var result = await _function.Run(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        AssertErrorCode(bad.Value, "missing_order_id");
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
    public async Task ReturnsResolutionResponse()
    {
        var resolutionRequest = CreateResolutionRequest();
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>()).Returns(resolutionRequest);
        _resolver.Resolve(resolutionRequest).Returns(new EffectiveDriverResolutionResult(DriverId, EffectiveDriverSource.RouteWeekday));

        var request = CreateHttpRequest(new { orderId = OrderId });

        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ResolveEffectiveDriverResponse>(ok.Value);
        Assert.Equal(OrderId, response.OrderId);
        Assert.Equal(DriverId, response.DriverId);
        Assert.Equal(nameof(EffectiveDriverSource.RouteWeekday), response.Source);
        Assert.False(response.IsUncovered);
    }

    [Fact]
    public async Task ReturnsUncoveredResolutionResponse()
    {
        var resolutionRequest = CreateResolutionRequest();
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>()).Returns(resolutionRequest);
        _resolver.Resolve(resolutionRequest).Returns(new EffectiveDriverResolutionResult(null, EffectiveDriverSource.UncoveredDueToDriverAbsence));

        var request = CreateHttpRequest(new { orderId = OrderId });

        var result = await _function.Run(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ResolveEffectiveDriverResponse>(ok.Value);
        Assert.Null(response.DriverId);
        Assert.Equal(nameof(EffectiveDriverSource.UncoveredDueToDriverAbsence), response.Source);
        Assert.True(response.IsUncovered);
    }

    [Fact]
    public async Task ReturnsNotFoundWhenOrderDoesNotExist()
    {
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Not found", null, HttpStatusCode.NotFound));

        var request = CreateHttpRequest(new { orderId = OrderId });

        var result = await _function.Run(request, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        AssertErrorCode(notFound.Value, "order_not_found");
    }

    [Fact]
    public async Task ReturnsUnprocessableEntityWhenResolutionContextIsInvalid()
    {
        _dataverse.GetEffectiveDriverResolutionRequestForOrderAsync(OrderId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Order has no customer assigned."));

        var request = CreateHttpRequest(new { orderId = OrderId });
        var result = await _function.Run(request, CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, error.StatusCode);
        AssertErrorCode(error.Value, "invalid_order_driver_context");
    }

    private static EffectiveDriverResolutionRequest CreateResolutionRequest()
        => new(
            AccountId,
            DeliveryDate,
            RouteId,
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
