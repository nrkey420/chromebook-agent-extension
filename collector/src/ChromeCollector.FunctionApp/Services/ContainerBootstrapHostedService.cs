using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Services;

/// <summary>
/// Creates the raw archive container at startup. Failure is logged, not fatal: right after a first deploy the
/// app's storage role assignment may not have propagated yet, and BlobWriter creates the container on first write.
/// </summary>
public sealed class ContainerBootstrapHostedService(IBlobWriter blobWriter, ILogger<ContainerBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await blobWriter.EnsureContainersExistAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create blob containers at startup; will retry on first write.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
