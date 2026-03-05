using System.Net;
using System.Text.Json;
using ChromebookCollector.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ChromebookCollector.Functions;

public sealed class CollectEventsFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HmacValidator _hmacValidator;
    private readonly RawBlobWriter _rawBlobWriter;
    private readonly SentinelIngestionClient _sentinelIngestionClient;
    private readonly ILogger<CollectEventsFunction> _logger;

    public CollectEventsFunction(
        HmacValidator hmacValidator,
        RawBlobWriter rawBlobWriter,
        SentinelIngestionClient sentinelIngestionClient,
        ILogger<CollectEventsFunction> logger)
    {
        _hmacValidator = hmacValidator;
        _rawBlobWriter = rawBlobWriter;
        _sentinelIngestionClient = sentinelIngestionClient;
        _logger = logger;
    }

    [Function("CollectEvents")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "v1/chrome/events/batch")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);
        var keyId = req.Headers.TryGetValues("X-Key-Id", out var keyVals) ? keyVals.FirstOrDefault() ?? string.Empty : string.Empty;
        var timestamp = req.Headers.TryGetValues("X-Timestamp", out var tsVals) ? tsVals.FirstOrDefault() ?? string.Empty : string.Empty;
        var signature = req.Headers.TryGetValues("X-Signature", out var sigVals) ? sigVals.FirstOrDefault() ?? string.Empty : string.Empty;
        var client = req.Headers.TryGetValues("X-Client", out var cVals) ? cVals.FirstOrDefault() ?? "unknown" : "unknown";

        if (!_hmacValidator.Validate(keyId, timestamp, signature, body))
        {
            var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauthorized.WriteStringAsync("invalid signature", cancellationToken);
            return unauthorized;
        }

        var payload = JsonSerializer.Deserialize<EventBatchRequest>(body, JsonOptions);
        if (payload?.Events is null || payload.Events.Count == 0)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("no events", cancellationToken);
            return badRequest;
        }

        await _rawBlobWriter.WriteJsonLineAsync(body, keyId, client, cancellationToken);
        var normalized = EventNormalizer.Normalize(payload.Events).ToList();
        await _sentinelIngestionClient.IngestAsync(normalized, cancellationToken);

        _logger.LogInformation("Accepted {Count} events", payload.Events.Count);
        var accepted = req.CreateResponse(HttpStatusCode.Accepted);
        await accepted.WriteStringAsync("accepted", cancellationToken);
        return accepted;
    }
}
