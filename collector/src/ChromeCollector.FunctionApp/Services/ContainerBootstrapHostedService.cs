using Microsoft.Extensions.Hosting;
namespace ChromeCollector.FunctionApp.Services;

public sealed class ContainerBootstrapHostedService(IBlobWriter blobWriter) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await blobWriter.EnsureContainersExistAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
