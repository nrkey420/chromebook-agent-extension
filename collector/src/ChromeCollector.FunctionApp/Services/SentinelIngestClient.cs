using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Services;

public interface ISentinelIngestClient
{
    Task<int> TryIngestAsync(IReadOnlyList<Dictionary<string, object?>> records, CancellationToken cancellationToken = default);
}

public sealed class SentinelIngestClient(HttpClient httpClient, IConfiguration configuration, ILogger<SentinelIngestClient> logger) : ISentinelIngestClient
{
    private readonly TokenCredential _credential = new DefaultAzureCredential();

    public async Task<int> TryIngestAsync(IReadOnlyList<Dictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var endpoint = configuration["DCE_ENDPOINT"];
        var dcrImmutableId = configuration["DCR_IMMUTABLE_ID"];
        var streamName = configuration["DCR_STREAM_NAME"];
        var resource = configuration["LOGS_INGESTION_RESOURCE"] ?? "https://monitor.azure.com/";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(dcrImmutableId) || string.IsNullOrWhiteSpace(streamName))
        {
            logger.LogWarning("Sentinel ingestion disabled because required settings are missing.");
            return 0;
        }

        var scope = resource.TrimEnd('/') + "/.default";
        var token = await _credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        var requestUri = $"{endpoint.TrimEnd('/')}/dataCollectionRules/{dcrImmutableId}/streams/{streamName}?api-version=2023-01-01";

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(JsonSerializer.Serialize(records), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return records.Count;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Sentinel ingestion failed with status {StatusCode}: {Error}", response.StatusCode, error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sentinel ingestion call failed.");
        }

        return 0;
    }
}
