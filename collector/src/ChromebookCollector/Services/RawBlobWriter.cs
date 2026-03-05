using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace ChromebookCollector.Services;

public sealed class RawBlobWriter
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly CollectorOptions _options;

    public RawBlobWriter(BlobServiceClient blobServiceClient, IOptions<CollectorOptions> options)
    {
        _blobServiceClient = blobServiceClient;
        _options = options.Value;
    }

    public async Task WriteJsonLineAsync(string rawPayload, string keyId, string client, CancellationToken cancellationToken)
    {
        var container = _blobServiceClient.GetBlobContainerClient(_options.RawContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var blobName = $"dt={now:yyyy-MM-dd}/hour={now:HH}/events-{Guid.NewGuid():N}.jsonl";
        var blob = container.GetBlobClient(blobName);
        var line = $"{rawPayload}\tkeyId={keyId}\tclient={client}\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line));
        await blob.UploadAsync(stream, overwrite: true, cancellationToken);
    }
}
