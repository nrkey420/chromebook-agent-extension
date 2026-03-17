using ChromeCollector.FunctionApp.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Services;

public interface ISqlWriter
{
    Task<int> WriteAsync(IReadOnlyList<ChromeEvent> events, string? publicIp, Guid correlationId, CancellationToken cancellationToken);
    Task LogErrorAsync(Guid correlationId, string layer, string code, string message, string? payload, CancellationToken cancellationToken);
}

public sealed class SqlWriter(IConfiguration configuration, ILogger<SqlWriter> logger, ISessionAttributionService attributionService) : ISqlWriter
{
    public async Task<int> WriteAsync(IReadOnlyList<ChromeEvent> events, string? publicIp, Guid correlationId, CancellationToken cancellationToken)
    {
        var conn = configuration["SQL_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(conn) || configuration["SQL_WRITE_ENABLED"]?.ToLowerInvariant() == "false") return 0;

        await using var sql = new SqlConnection(conn);
        await sql.OpenAsync(cancellationToken);
        var writes = 0;

        foreach (var e in events)
        {
            var sessionId = e.SessionId ?? Guid.NewGuid();
            var cmd = sql.CreateCommand();
            cmd.CommandText = @"
MERGE Sessions AS t USING (SELECT @SessionId SessionId) s ON t.SessionId=s.SessionId
WHEN MATCHED THEN UPDATE SET LastSeenUtc=@Now, UpdatedUtc=@Now, IsActive = CASE WHEN @EventType IN ('LOGOUT','SESSION_END') THEN 0 ELSE t.IsActive END
WHEN NOT MATCHED THEN INSERT (SessionId,DirectoryDeviceId,DeviceSerial,UserEmail,SessionStartTimeUtc,LastSeenUtc,InternalIp,PublicIp,AttributionConfidence,ExtensionVersion,OrgUnit,School,IsActive,CreatedUtc,UpdatedUtc)
VALUES (@SessionId,@DirectoryDeviceId,@DeviceSerial,@UserEmail,@Now,@Now,@InternalIp,@PublicIp,@Confidence,@ExtVersion,@OrgUnit,@School,1,@Now,@Now);
INSERT INTO ActivityEvents(SessionId,DirectoryDeviceId,UserEmail,EventType,EventTimeUtc,Url,Domain,Title,DownloadFileName,DownloadMime,DownloadDanger,DownloadState,InternalIp,PublicIp,IngestCorrelationId,CreatedUtc)
VALUES(@SessionId,@DirectoryDeviceId,@UserEmail,@EventType,@EventTime,@Url,@Domain,@Title,@DownloadFileName,@DownloadMime,@DownloadDanger,@DownloadState,@InternalIp,@PublicIp,@CorrelationId,@Now);";
            var now = DateTime.UtcNow;
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
            cmd.Parameters.AddWithValue("@DirectoryDeviceId", (object?)e.DirectoryDeviceId ?? "unknown");
            cmd.Parameters.AddWithValue("@DeviceSerial", (object?)e.DeviceSerial ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserEmail", (object?)e.UserEmail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@InternalIp", e.Extra?.TryGetValue("internalIp", out var i) == true ? i.ToString()! : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@PublicIp", (object?)publicIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Confidence", attributionService.CalculateConfidence(e));
            cmd.Parameters.AddWithValue("@ExtVersion", (object?)e.ExtensionVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OrgUnit", e.Extra?.TryGetValue("orgUnit", out var o) == true ? o.ToString()! : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@School", e.Extra?.TryGetValue("school", out var s) == true ? s.ToString()! : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@EventType", e.EventType ?? "UNKNOWN");
            cmd.Parameters.AddWithValue("@EventTime", e.EventTimeUtc?.UtcDateTime ?? now);
            cmd.Parameters.AddWithValue("@Url", (object?)e.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Domain", (object?)e.Domain ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Title", (object?)e.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DownloadFileName", (object?)e.DownloadFileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DownloadMime", (object?)e.DownloadMime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DownloadDanger", (object?)e.DownloadDanger ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DownloadState", (object?)e.DownloadState ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CorrelationId", correlationId);
            cmd.Parameters.AddWithValue("@Now", now);

            writes += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return writes;
    }

    public async Task LogErrorAsync(Guid correlationId, string layer, string code, string message, string? payload, CancellationToken cancellationToken)
    {
        try
        {
            var conn = configuration["SQL_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(conn)) return;
            await using var sql = new SqlConnection(conn);
            await sql.OpenAsync(cancellationToken);
            await using var cmd = sql.CreateCommand();
            cmd.CommandText = "INSERT INTO IngestionErrors(ErrorTimeUtc,CorrelationId,Layer,ErrorCode,ErrorMessage,PayloadFragment) VALUES(@t,@c,@l,@e,@m,@p)";
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@c", correlationId);
            cmd.Parameters.AddWithValue("@l", layer);
            cmd.Parameters.AddWithValue("@e", code);
            cmd.Parameters.AddWithValue("@m", message);
            cmd.Parameters.AddWithValue("@p", (object?)payload ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log ingestion error.");
        }
    }
}
