using Azure.Identity;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(new DefaultAzureCredential());
builder.Services.AddHttpClient<IDocumentMergeService, DocumentMergeService>();

builder.Build().Run();
