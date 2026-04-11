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
    // The Application Insights SDK adds a default logging filter that instructs ILogger to capture only Warning and more severe logs.
    // Replace that default with explicit category filters so framework Information logs are not sent to Application Insights.
    const string applicationInsightsProvider = "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider";

    LoggerFilterRule? defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName == applicationInsightsProvider);
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }

    options.Rules.Add(new LoggerFilterRule(applicationInsightsProvider, "Microsoft", LogLevel.Warning, null));
    options.Rules.Add(new LoggerFilterRule(applicationInsightsProvider, "System", LogLevel.Warning, null));
    options.Rules.Add(new LoggerFilterRule(applicationInsightsProvider, "Manufaktura.Orders.Functions", LogLevel.Information, null));
});

builder.Services.AddSingleton<DefaultAzureCredential>();
builder.Services.AddSingleton<TokenCredential>(sp => sp.GetRequiredService<DefaultAzureCredential>());
builder.Services.AddHttpClient<IDocumentMergeService, DocumentMergeService>();
builder.Services.AddHttpClient<IDataverseService, DataverseService>();
builder.Services.AddHttpClient<ISharePointService, SharePointService>();

builder.Build().Run();
