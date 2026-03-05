using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using ChromebookCollector.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
              .AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient();
        services.AddSingleton<TokenCredential, DefaultAzureCredential>();
        services.AddSingleton(_ =>
        {
            var storage = context.Configuration["AzureWebJobsStorage"];
            return new BlobServiceClient(storage);
        });

        services.Configure<CollectorOptions>(context.Configuration);
        services.AddSingleton<HmacValidator>();
        services.AddSingleton<RawBlobWriter>();
        services.AddSingleton<SentinelIngestionClient>();
    })
    .Build();

host.Run();
