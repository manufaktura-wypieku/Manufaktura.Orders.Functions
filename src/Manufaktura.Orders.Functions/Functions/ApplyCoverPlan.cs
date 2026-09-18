using System.Net;
using System.Text.Json;
using Manufaktura.Orders.Functions.Models;
using Manufaktura.Orders.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Functions;

public class ApplyCoverPlan
{
    private const int MaximumNameLength = 850;

    private readonly IDataverseService _dataverse;
    private readonly IEffectiveDriverRefresher _refresher;
    private readonly ILogger<ApplyCoverPlan> _logger;

    public ApplyCoverPlan(IDataverseService dataverse, IEffectiveDriverRefresher refresher, ILogger<ApplyCoverPlan> logger)
    {
        _dataverse = dataverse;
        _refresher = refresher;
        _logger = logger;
    }

    [Function("ApplyCoverPlan")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        ApplyCoverPlanRequest? request;
        try
        {
            request = await req.ReadFromJsonAsync<ApplyCoverPlanRequest>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize ApplyCoverPlan request body");
            return new BadRequestObjectResult(new { error = "Request body contains invalid JSON.", code = "invalid_json" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ApplyCoverPlan request has unsupported or missing Content-Type");
            return new ObjectResult(new { error = "Request Content-Type must be application/json.", code = "unsupported_media_type" })
            {
                StatusCode = StatusCodes.Status415UnsupportedMediaType
            };
        }

        if (request is null)
            return new BadRequestObjectResult(new { error = "Request body is required.", code = "invalid_json" });

        var validationError = Validate(request);
        if (validationError is not null)
            return validationError;

        var accountIds = request.AccountIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        try
        {
            var overlaps = await _dataverse.GetOverlappingAccountDeliveryOverridesAsync(accountIds, request.FromDate, request.ToDate, cancellationToken);
            var conflictingAccountIds = accountIds
                .Where(accountId => overlaps.Any(overlap => overlap.AccountId == accountId && !IsIdentical(overlap, accountId, request.DriverId, request.FromDate, request.ToDate)))
                .ToArray();
            if (conflictingAccountIds.Length > 0)
            {
                return new ConflictObjectResult(new
                {
                    error = $"{conflictingAccountIds.Length} selected account{(conflictingAccountIds.Length == 1 ? " has" : "s have")} overlapping cover.",
                    code = "overlapping_cover",
                    conflictingAccountCount = conflictingAccountIds.Length
                });
            }

            var alreadyCovered = accountIds
                .Where(accountId => overlaps.Any(overlap => IsIdentical(overlap, accountId, request.DriverId, request.FromDate, request.ToDate)))
                .ToArray();
            var toCreate = accountIds.Except(alreadyCovered).ToArray();
            var createdAccountIds = new List<Guid>();
            var warnings = new List<string>();

            foreach (var accountId in toCreate)
            {
                try
                {
                    var name = BuildOverrideName(request, accountId);
                    await _dataverse.CreateAccountDeliveryOverrideAsync(accountId, request.DriverId, request.FromDate, request.ToDate, name, cancellationToken);
                    createdAccountIds.Add(accountId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not create account delivery override for {AccountId}", accountId);
                    warnings.Add($"Could not create cover for account {accountId:D}.");
                }
            }

            var accountsToRefresh = alreadyCovered.Concat(createdAccountIds).Distinct().ToArray();
            RefreshEffectiveDriversResponse? refresh = null;
            if (accountsToRefresh.Length > 0)
            {
                refresh = await _refresher.RefreshAsync(new RefreshEffectiveDriversRequest
                {
                    AccountIds = accountsToRefresh,
                    FromDate = request.FromDate,
                    ToDate = request.ToDate
                }, cancellationToken);
                warnings.AddRange(refresh.Results.Where(result => result.Status == "failed").Select(result => result.Error ?? $"Order {result.OrderId:D} could not be refreshed."));
                warnings.AddRange(refresh.Results.SelectMany(result => result.Warnings ?? []));
                if (refresh.Status != "complete")
                    warnings.Add("Refresh did not cover every matching order.");
            }
            else if (toCreate.Length > 0)
            {
                warnings.Add("No cover was saved, so no orders were refreshed.");
            }

            var failedOrders = refresh?.Results.Count(result => result.Status == "failed") ?? 0;
            var status = warnings.Count == 0 && failedOrders == 0 && refresh?.Status != "incomplete"
                ? "complete"
                : "incomplete";

            return new OkObjectResult(new ApplyCoverPlanResponse(
                status,
                createdAccountIds.Count,
                alreadyCovered.Length,
                refresh?.UpdatedOrders ?? 0,
                refresh?.DeliveryPacksRegenerated ?? 0,
                refresh?.LockedPacksRequiringReview ?? 0,
                warnings));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(ex, "Unauthorized access to Dataverse in ApplyCoverPlan");
            return new ObjectResult(new { error = "Upstream service access denied.", code = "upstream_auth_failure" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Upstream service error in ApplyCoverPlan");
            return new ObjectResult(new { error = "An upstream service returned an error.", code = "upstream_error" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in ApplyCoverPlan");
            return new ObjectResult(new { error = "An unexpected error occurred.", code = "internal_error" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private static ObjectResult? Validate(ApplyCoverPlanRequest request)
    {
        if (request.DriverId == Guid.Empty)
            return new BadRequestObjectResult(new { error = "DriverId is required.", code = "missing_driver_id" });

        if (request.FromDate == default || request.ToDate == default || request.FromDate > request.ToDate)
            return new BadRequestObjectResult(new { error = "A valid date range is required.", code = "invalid_date_range" });

        if (request.AccountIds is null || request.AccountIds.Length == 0 || request.AccountIds.All(id => id == Guid.Empty))
            return new BadRequestObjectResult(new { error = "At least one account is required.", code = "missing_account_ids" });

        return null;
    }

    private static bool IsIdentical(AccountDeliveryOverrideRecord overlap, Guid accountId, Guid driverId, DateOnly fromDate, DateOnly toDate)
        => overlap.AccountId == accountId && overlap.DriverId == driverId && overlap.FromDate == fromDate && overlap.ToDate == toDate;

    private static string BuildOverrideName(ApplyCoverPlanRequest request, Guid accountId)
    {
        var driverName = string.IsNullOrWhiteSpace(request.DriverName) ? request.DriverId.ToString("D") : request.DriverName.Trim();
        var accountName = LookupName(request.AccountNames, accountId) ?? accountId.ToString("D");
        var name = $"Driver Rota - {driverName} - {accountName}";
        return name.Length <= MaximumNameLength ? name : name[..MaximumNameLength];
    }

    private static string? LookupName(Dictionary<string, string>? names, Guid id)
    {
        if (names is null)
            return null;

        if (names.TryGetValue(id.ToString("D"), out var formatted) && !string.IsNullOrWhiteSpace(formatted))
            return formatted.Trim();

        foreach (var pair in names)
        {
            if (Guid.TryParse(pair.Key, out var key) && key == id && !string.IsNullOrWhiteSpace(pair.Value))
                return pair.Value.Trim();
        }

        return null;
    }
}
