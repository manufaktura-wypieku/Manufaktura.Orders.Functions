using Manufaktura.Orders.Functions.Models;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Services;

public sealed class EmptyOrderGenerator
{
    private readonly IDataverseService _dataverse;
    private readonly ILogger _logger;

    public EmptyOrderGenerator(IDataverseService dataverse, ILogger logger)
    {
        _dataverse = dataverse;
        _logger = logger;
    }

    public async Task GenerateAsync(DateOnly ukToday, CancellationToken cancellationToken = default)
    {
        var today = await GenerateForDateAsync(ukToday, cancellationToken);
        var tomorrow = await GenerateForDateAsync(ukToday.AddDays(1), cancellationToken);

        var failed = today.Failed + tomorrow.Failed;
        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"Empty order generation failed for {failed} account(s). Created orders were kept.");
        }
    }

    private async Task<EmptyOrderPassResult> GenerateForDateAsync(DateOnly deliveryDate, CancellationToken cancellationToken)
    {
        var accounts = await _dataverse.ListActiveAccountsForDeliveryDateAsync(deliveryDate, cancellationToken);
        var existing = await _dataverse.ListAccountIdsWithOrderOnDateAsync(deliveryDate, cancellationToken);

        var created = 0;
        var alreadyPresent = 0;
        var missingPriceList = 0;
        var failed = 0;

        foreach (var account in accounts)
        {
            var accountName = string.IsNullOrWhiteSpace(account.Name)
                ? account.AccountId.ToString("D")
                : account.Name;

            if (account.PriceListId is null)
            {
                missingPriceList++;
                _logger.LogWarning(
                    "Skipped account {AccountName} ({AccountId}): no price list. Delivery date {DeliveryDate}.",
                    accountName,
                    account.AccountId,
                    deliveryDate);
                continue;
            }

            if (existing.Contains(account.AccountId))
            {
                alreadyPresent++;
                continue;
            }

            try
            {
                await _dataverse.CreateEmptyOrderAsync(account.AccountId, account.PriceListId.Value, deliveryDate, cancellationToken);
                created++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "Failed to create empty order for account {AccountName} ({AccountId}) on {DeliveryDate}.",
                    accountName,
                    account.AccountId,
                    deliveryDate);
            }
        }

        _logger.LogInformation(
            "Empty orders for {DeliveryDate}: created={Created}, alreadyPresent={AlreadyPresent}, missingPriceList={MissingPriceList}, failed={Failed}",
            deliveryDate,
            created,
            alreadyPresent,
            missingPriceList,
            failed);

        return new EmptyOrderPassResult(deliveryDate, created, alreadyPresent, missingPriceList, failed);
    }
}
