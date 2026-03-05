using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChromebookCollector.Services;

public sealed class SentinelIngestionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly CollectorOptions _options;
    private readonly ILogger<SentinelIngestionClient> _logger;

    public SentinelIngestionClient(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IOptions<CollectorOptions> options,
        ILogger<SentinelIngestionClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _options = options.Value;
        _logger = logger;
    }

    public async Task IngestAsync(IEnumerable<SentinelRecord> records, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SentinelEndpoint) || string.IsNullOrWhiteSpace(_options.SentinelDcrImmutableId))
        {
            _logger.LogInformation("Sentinel config missing; skipping ingestion.");
            return;
        }

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://monitor.azure.com/.default"]),
            cancellationToken);

        var endpoint = $"{_options.SentinelEndpoint.TrimEnd('/')}/dataCollectionRules/{_options.SentinelDcrImmutableId}/streams/{_options.SentinelStreamName}?api-version=2023-01-01";
        var payload = JsonSerializer.Serialize(records, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var client = _httpClientFactory.CreateClient(nameof(SentinelIngestionClient));
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Sentinel ingestion failed: {StatusCode} {Body}", response.StatusCode, body);
        }
    }
}
