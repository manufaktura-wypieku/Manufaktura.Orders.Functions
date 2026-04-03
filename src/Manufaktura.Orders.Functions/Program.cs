using Azure.Identity;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(new DefaultAzureCredential());
builder.Services.AddHttpClient<DocumentMergeService>();
builder.Services.AddSingleton<IDocumentMergeService>(sp =>
    sp.GetRequiredService<DocumentMergeService>());

builder.Build().Run();
