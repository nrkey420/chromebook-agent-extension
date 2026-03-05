using System.Net;
using System.Text;
using System.Text.Json;
using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Functions;

public sealed class IngestBatchFunction(
    IHmacAuth hmacAuth,
    IRequestRateLimiter rateLimiter,
    IPayloadNormalizer payloadNormalizer,
    IBlobWriter blobWriter,
    ISentinelIngestClient sentinelIngestClient,
    ILogger<IngestBatchFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxBodySizeBytes = 1_048_576;

    [Function("IngestBatch")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/chrome/events/batch")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var correlationId = context.InvocationId;

        if (!TryGetHeader(request, "X-Timestamp", out var timestamp)
            || !TryGetHeader(request, "X-Signature", out var signature)
            || !TryGetHeader(request, "X-Key-Id", out var keyId))
        {
            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, "Missing required headers.", cancellationToken);
        }

        if (!rateLimiter.TryConsume(keyId!))
        {
            logger.LogWarning("Rate limit exceeded for keyId {KeyId} correlationId {CorrelationId}.", keyId, correlationId);
            return await CreateErrorResponse(request, (HttpStatusCode)429, "Rate limit exceeded.", cancellationToken);
        }

        byte[] rawBytes;
        using (var ms = new MemoryStream())
        {
            await request.Body.CopyToAsync(ms, cancellationToken);
            if (ms.Length > MaxBodySizeBytes)
            {
                logger.LogWarning("Payload too large for keyId {KeyId} correlationId {CorrelationId}.", keyId, correlationId);
                return await CreateErrorResponse(request, HttpStatusCode.RequestEntityTooLarge, "Payload exceeds 1MB limit.", cancellationToken);
            }

            rawBytes = ms.ToArray();
        }

        if (!hmacAuth.TryValidate(keyId!, timestamp!, signature!, rawBytes, out var authError))
        {
            return await CreateErrorResponse(request, HttpStatusCode.Unauthorized, authError ?? "Unauthorized", cancellationToken);
        }

        ChromeBatch? batch;
        try
        {
            batch = JsonSerializer.Deserialize<ChromeBatch>(rawBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, "Invalid JSON payload.", cancellationToken);
        }

        if (batch?.Events is null)
        {
            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, "Batch must include at least one event.", cancellationToken);
        }

        if (!BatchSchemaValidator.TryValidate(batch, out var schemaError))
        {
            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, schemaError ?? "Schema validation failed.", cancellationToken);
        }

        var rawLines = batch.Events.Select(e => JsonSerializer.Serialize(e, JsonOptions)).ToList();
        var rawPath = await blobWriter.WriteJsonLinesAsync(BlobWriter.RawContainer, rawLines, "events", cancellationToken);

        var clientIp = TryGetClientIp(request);
        var normalized = batch.Events.Select(e => payloadNormalizer.Normalize(e, keyId!, clientIp)).ToList();
        var normalizedLines = normalized.Select(x => JsonSerializer.Serialize(x, JsonOptions));
        await blobWriter.WriteJsonLinesAsync(BlobWriter.NormalizedContainer, normalizedLines, "normalized", cancellationToken);

        var ingested = await sentinelIngestClient.TryIngestAsync(normalized, cancellationToken);
        if (ingested < normalized.Count)
        {
            logger.LogWarning("Only {Ingested} of {Total} records ingested to Sentinel for correlationId {CorrelationId}.", ingested, normalized.Count, correlationId);
        }

        logger.LogInformation(
            "Accepted batch correlationId {CorrelationId} keyId {KeyId} eventCount {EventCount} contentLength {ContentLength}.",
            correlationId,
            keyId,
            batch.Events.Count,
            rawBytes.Length);

        var response = request.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            accepted = batch.Events.Count,
            ingested,
            rawBlobPath = rawPath,
            correlationId
        }, cancellationToken);
        return response;
    }

    private static bool TryGetHeader(HttpRequestData request, string headerName, out string? value)
    {
        value = null;
        if (!request.Headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        value = values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? TryGetClientIp(HttpRequestData request)
    {
        if (request.Headers.TryGetValues("X-Forwarded-For", out var values))
        {
            var combined = values.FirstOrDefault();
            return combined?.Split(',').FirstOrDefault()?.Trim();
        }

        return null;
    }

    private static async Task<HttpResponseData> CreateErrorResponse(HttpRequestData request, HttpStatusCode statusCode, string message, CancellationToken cancellationToken)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message }, cancellationToken);
        return response;
    }
}
