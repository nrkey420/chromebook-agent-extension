using System.Net;
using System.Text.Json;
using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Functions;

public sealed class IngestBatchFunction(
    IConfiguration configuration,
    IHmacAuth hmacAuth,
    IRequestRateLimiter rateLimiter,
    IPayloadNormalizer payloadNormalizer,
    IBlobWriter blobWriter,
    ISentinelIngestClient sentinelIngestClient,
    ISqlWriter sqlWriter,
    IPublicIpResolver publicIpResolver,
    ISessionAttributionService sessionAttributionService,
    ILogger<IngestBatchFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("IngestBatch")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v1/chrome/events/batch")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var correlationId = context.InvocationId;
        var maxBodySize = int.TryParse(configuration["MAX_BODY_BYTES"], out var m) ? m : 1_048_576;

        if (!TryGetHeader(request, "X-Timestamp", out var timestamp)
            || !TryGetHeader(request, "X-Signature", out var signature)
            || !TryGetHeader(request, "X-Key-Id", out var keyId))
        {
            return await Error(request, HttpStatusCode.BadRequest, "Missing required headers", cancellationToken);
        }

        TryGetHeader(request, "X-Device-Id", out var deviceHeader);
        if (!rateLimiter.TryConsume($"{keyId}:{deviceHeader ?? "unknown"}"))
            return await Error(request, (HttpStatusCode)429, "Rate limit exceeded", cancellationToken);

        await using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, cancellationToken);
        if (ms.Length > maxBodySize) return await Error(request, HttpStatusCode.RequestEntityTooLarge, "Payload exceeds MAX_BODY_BYTES", cancellationToken);
        var raw = ms.ToArray();

        if (!hmacAuth.TryValidate(keyId!, timestamp!, signature!, raw, out var authError))
            return await Error(request, HttpStatusCode.Unauthorized, authError ?? "Unauthorized", cancellationToken);

        ChromeBatch? batch;
        try { batch = JsonSerializer.Deserialize<ChromeBatch>(raw, JsonOptions); }
        catch (JsonException) { return await Error(request, HttpStatusCode.BadRequest, "Invalid JSON", cancellationToken); }

        if (batch is null) return await Error(request, HttpStatusCode.BadRequest, "Batch empty", cancellationToken);
        if (!BatchSchemaValidator.TryValidate(batch, out var schemaError))
            return await Error(request, HttpStatusCode.BadRequest, schemaError ?? "Invalid batch", cancellationToken);

        // Raw copy first: it is the system of record and allows replay if SQL is unavailable.
        var deviceId = batch.Events.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.DirectoryDeviceId))?.DirectoryDeviceId ?? "unknown";
        var rawPath = await blobWriter.WriteJsonLinesAsync(
            BlobWriter.RawContainer,
            batch.Events.Select(e => JsonSerializer.Serialize(e, JsonOptions)),
            $"{BlobWriter.SafeSegment(keyId!)}/{BlobWriter.SafeSegment(deviceId)}",
            cancellationToken);

        var publicIp = publicIpResolver.Resolve(request);
        foreach (var e in batch.Events) EventEnricher.Enrich(e);

        var sqlWrites = 0;
        if (sqlWriter.IsEnabled)
        {
            try
            {
                sqlWrites = await sqlWriter.WriteAsync(batch.Events, publicIp, correlationId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SQL write failed correlationId {corr}", correlationId);
                await sqlWriter.LogErrorAsync(correlationId, "SQL", "WRITE_FAILED", ex.Message, rawPath, cancellationToken);
                // Ask the extension to retry; EventId de-duplication makes the retry safe.
                return await Error(request, HttpStatusCode.ServiceUnavailable, "Storage temporarily unavailable", cancellationToken);
            }
        }

        var sentinelWrites = 0;
        try
        {
            var normalized = batch.Events
                .Select(e => payloadNormalizer.Normalize(e, keyId!, publicIp, sessionAttributionService.CalculateConfidence(e)))
                .ToList();
            sentinelWrites = await sentinelIngestClient.TryIngestAsync(normalized, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentinel ingest failed correlationId {corr}", correlationId);
            await sqlWriter.LogErrorAsync(correlationId, "Sentinel", "INGEST_FAILED", ex.Message, rawPath, cancellationToken);
        }

        var response = request.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { accepted = batch.Events.Count, sqlWrites, sentinelWrites, correlationId }, cancellationToken);
        response.StatusCode = HttpStatusCode.Accepted;
        logger.LogInformation("Accepted {count} events ({sql} new in SQL) correlationId {corr}", batch.Events.Count, sqlWrites, correlationId);
        return response;
    }

    private static bool TryGetHeader(HttpRequestData request, string key, out string? value)
    {
        value = null;
        if (!request.Headers.TryGetValues(key, out var values)) return false;
        value = values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, string message, CancellationToken ct)
    {
        var r = req.CreateResponse(code);
        await r.WriteAsJsonAsync(new { error = message }, ct);
        // WriteAsJsonAsync resets the status to 200; restore it.
        r.StatusCode = code;
        return r;
    }
}
