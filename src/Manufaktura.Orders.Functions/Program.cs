using Azure.Core;
using Azure.Identity;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    // The Application Insights SDK adds a default logging filter that instructs ILogger to capture only Warning and more severe logs. Application Insights requires an explicit override.
    LoggerFilterRule? defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName
        == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

builder.Services.AddSingleton<DefaultAzureCredential>();
builder.Services.AddSingleton<TokenCredential>(sp => sp.GetRequiredService<DefaultAzureCredential>());
builder.Services.AddHttpClient<IDocumentMergeService, DocumentMergeService>();
builder.Services.AddHttpClient<IDataverseService, DataverseService>();
builder.Services.AddHttpClient<ISharePointService, SharePointService>();

builder.Build().Run();
