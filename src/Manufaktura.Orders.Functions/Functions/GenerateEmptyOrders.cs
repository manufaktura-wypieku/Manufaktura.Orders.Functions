using Manufaktura.Orders.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Manufaktura.Orders.Functions.Functions;

public class GenerateEmptyOrders
{
    private readonly EmptyOrderGenerator _generator;
    private readonly TimeProvider _clock;
    private readonly ILogger<GenerateEmptyOrders> _logger;

    public GenerateEmptyOrders(IDataverseService dataverse, TimeProvider clock, ILogger<GenerateEmptyOrders> logger)
    {
        _generator = new EmptyOrderGenerator(dataverse, logger);
        _clock = clock;
        _logger = logger;
    }

    // 01:00 UTC is 02:00 during British Summer Time. 02:00 UTC is 02:00 during Greenwich Mean Time.
    // On the autumn clock change the repeated hour is 01:00, so 02:00 London still happens once.
    [Function("GenerateEmptyOrders")]
    public async Task Run([TimerTrigger("0 0 1,2 * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (!EmptyOrderSchedule.ShouldRun(now, timer.IsPastDue))
        {
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
            _logger.LogInformation("Skipped scheduled empty-order run. UK local time is {LocalTime}.", local);
            return;
        }

        await _generator.GenerateAsync(EmptyOrderSchedule.UkDate(now), cancellationToken);
    }
}
