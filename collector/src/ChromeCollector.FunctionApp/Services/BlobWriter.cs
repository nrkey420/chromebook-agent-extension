using System.Text;
using Azure;
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

    public async Task EnsureContainersExistAsync(CancellationToken cancellationToken = default)
    {
        await blobServiceClient.GetBlobContainerClient(RawContainer).CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<string> WriteJsonLinesAsync(string containerName, IEnumerable<string> lines, string prefix, CancellationToken cancellationToken = default)
    {
        var container = blobServiceClient.GetBlobContainerClient(containerName);
        var blobName = $"{prefix}/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}.jsonl";
        var blobClient = container.GetBlobClient(blobName);

        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        try
        {
            await blobClient.UploadAsync(new BinaryData(bytes), overwrite: true, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "ContainerNotFound")
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await blobClient.UploadAsync(new BinaryData(bytes), overwrite: true, cancellationToken);
        }

        return $"{containerName}/{blobName}";
    }

    /// <summary>Makes a client-supplied value safe to use as one blob path segment.</summary>
    public static string SafeSegment(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var s = new string(chars).Trim('.');
        if (s.Length > 128) s = s[..128];
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }
}
