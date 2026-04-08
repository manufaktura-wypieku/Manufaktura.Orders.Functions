using Azure.Core;
using Azure.Identity;
using Manufaktura.Orders.Functions.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton<DefaultAzureCredential>();
builder.Services.AddSingleton<TokenCredential>(sp => sp.GetRequiredService<DefaultAzureCredential>());
builder.Services.AddHttpClient<IDocumentMergeService, DocumentMergeService>();
builder.Services.AddHttpClient<IDataverseService, DataverseService>();
builder.Services.AddHttpClient<ISharePointService, SharePointService>();

builder.Build().Run();
