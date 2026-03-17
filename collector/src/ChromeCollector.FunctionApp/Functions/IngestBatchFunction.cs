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

        if (!rateLimiter.TryConsume(keyId!)) return await Error(request, (HttpStatusCode)429, "Rate limit exceeded", cancellationToken);

        await using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, cancellationToken);
        if (ms.Length > maxBodySize) return await Error(request, HttpStatusCode.RequestEntityTooLarge, "Payload exceeds MAX_BODY_BYTES", cancellationToken);
        var raw = ms.ToArray();

        if (!hmacAuth.TryValidate(keyId!, timestamp!, signature!, raw, out var authError))
            return await Error(request, HttpStatusCode.Unauthorized, authError ?? "Unauthorized", cancellationToken);

        ChromeBatch? batch;
        try { batch = JsonSerializer.Deserialize<ChromeBatch>(raw, JsonOptions); }
        catch { return await Error(request, HttpStatusCode.BadRequest, "Invalid JSON", cancellationToken); }

        if (batch?.Events is null || batch.Events.Count == 0) return await Error(request, HttpStatusCode.BadRequest, "Batch empty", cancellationToken);

        var deviceId = batch.Events.FirstOrDefault()?.DirectoryDeviceId ?? "unknown";
        var rawPath = await blobWriter.WriteJsonLinesAsync(BlobWriter.RawContainer, batch.Events.Select(e => JsonSerializer.Serialize(e)), $"{keyId}/{deviceId}", cancellationToken);

        var clientIp = request.Headers.TryGetValues("X-Forwarded-For", out var xff) ? xff.FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim() : null;
        var publicIp = publicIpResolver.Resolve(request);
        var normalized = batch.Events.Select(e => payloadNormalizer.Normalize(e, keyId!, clientIp, publicIp, sessionAttributionService.CalculateConfidence(e))).ToList();

        await blobWriter.WriteJsonLinesAsync(BlobWriter.NormalizedContainer, normalized.Select(n => JsonSerializer.Serialize(n)), $"{keyId}/{deviceId}", cancellationToken);

        var sqlWrites = 0;
        try { sqlWrites = await sqlWriter.WriteAsync(batch.Events, publicIp, correlationId, cancellationToken); }
        catch (Exception ex) { await sqlWriter.LogErrorAsync(correlationId, "SQL", "WRITE_FAILED", ex.Message, null, cancellationToken); }

        var sentinelWrites = 0;
        try { sentinelWrites = await sentinelIngestClient.TryIngestAsync(normalized, cancellationToken); }
        catch (Exception ex) { await sqlWriter.LogErrorAsync(correlationId, "Sentinel", "INGEST_FAILED", ex.Message, null, cancellationToken); }

        var response = request.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { accepted = batch.Events.Count, blobPath = rawPath, sqlWrites, sentinelWrites, correlationId }, cancellationToken);
        logger.LogInformation("Accepted {count} events correlationId {corr}", batch.Events.Count, correlationId);
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
        return r;
    }
}
