using System.Text;
using Azure.Storage.Blobs;

namespace ChromeCollector.FunctionApp.Services;

public interface IBlobWriter
{
    Task EnsureContainersExistAsync(CancellationToken cancellationToken = default);
    Task<string> WriteJsonLinesAsync(string containerName, IEnumerable<string> lines, string prefix, CancellationToken cancellationToken = default);
}

public sealed class BlobWriter(BlobServiceClient blobServiceClient) : IBlobWriter
{
    public const string RawContainer = "chrome-activity-raw";
    public const string NormalizedContainer = "chrome-activity-normalized";

    public async Task EnsureContainersExistAsync(CancellationToken cancellationToken = default)
    {
        await blobServiceClient.GetBlobContainerClient(RawContainer).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await blobServiceClient.GetBlobContainerClient(NormalizedContainer).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<string> WriteJsonLinesAsync(string containerName, IEnumerable<string> lines, string prefix, CancellationToken cancellationToken = default)
    {
        var container = blobServiceClient.GetBlobContainerClient(containerName);
        var blobName = $"{prefix}/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}.jsonl";
        var blobClient = container.GetBlobClient(blobName);

        var content = string.Join('\n', lines);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true, cancellationToken);

        return $"{containerName}/{blobName}";
    }
}
