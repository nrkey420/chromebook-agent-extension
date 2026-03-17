using Azure.Storage.Blobs;
using ChromeCollector.FunctionApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();
    })
    .ConfigureServices((_, services) =>
    {
        services.AddLogging();

        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration["AzureWebJobsStorage"]
                ?? configuration.GetConnectionString("AzureWebJobsStorage")
                ?? throw new InvalidOperationException("AzureWebJobsStorage must be configured.");

            return new BlobServiceClient(connectionString);
        });

        services.AddHttpClient<ISentinelIngestClient, SentinelIngestClient>();
        services.AddSingleton<IHmacAuth, HmacAuth>();
        services.AddSingleton<IRequestRateLimiter, InMemoryTokenBucketRateLimiter>();
        services.AddSingleton<IPayloadNormalizer, PayloadNormalizer>();
        services.AddSingleton<IPublicIpResolver, PublicIpResolver>();
        services.AddSingleton<ISessionAttributionService, SessionAttributionService>();
        services.AddSingleton<ISqlWriter, SqlWriter>();
        services.AddSingleton<IBlobWriter, BlobWriter>();
        services.AddHostedService<ContainerBootstrapHostedService>();
    })
    .Build();

host.Run();
