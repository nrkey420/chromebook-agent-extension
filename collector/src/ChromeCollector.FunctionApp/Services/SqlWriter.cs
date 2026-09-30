using System.Data;
using ChromeCollector.FunctionApp.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChromeCollector.FunctionApp.Services;

public interface ISqlWriter
{
    bool IsEnabled { get; }
    Task<int> WriteAsync(IReadOnlyList<ChromeEvent> events, string? publicIp, string correlationId, CancellationToken cancellationToken);
    Task LogErrorAsync(string correlationId, string layer, string code, string message, string? blobPath, CancellationToken cancellationToken);
}

public sealed class SqlWriter(IConfiguration configuration, ILogger<SqlWriter> logger) : ISqlWriter
{
    // Event types that record where the device was on the network.
    private static readonly HashSet<string> IpObservationEventTypes = ["SESSION_START", "LOGIN", "HEARTBEAT", "NETWORK_CHANGE", "UNLOCK"];

    // Inserts one extension event. Duplicate EventIds (client retries) are skipped entirely.
    // Returns 1 when inserted, NULL when it was a duplicate.
    public const string InsertEventSql = """
IF @EventId IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.ActivityEvents WHERE EventId = @EventId)
BEGIN
  IF @SessionId IS NOT NULL
  BEGIN
    MERGE dbo.Sessions WITH (HOLDLOCK) AS t
    USING (SELECT @SessionId AS SessionId) AS s ON t.SessionId = s.SessionId
    WHEN MATCHED THEN UPDATE SET
      SessionStartUtc = CASE WHEN @EventTime < t.SessionStartUtc THEN @EventTime ELSE t.SessionStartUtc END,
      LastSeenUtc = CASE WHEN @EventTime > t.LastSeenUtc THEN @EventTime ELSE t.LastSeenUtc END,
      LoginUtc = CASE WHEN @EventType = 'LOGIN' AND t.LoginUtc IS NULL THEN @EventTime ELSE t.LoginUtc END,
      LogoutUtc = CASE WHEN @EventType = 'LOGOUT' THEN @EventTime ELSE t.LogoutUtc END,
      SessionEndUtc = CASE WHEN @EventType = 'SESSION_END' THEN @EventTime ELSE t.SessionEndUtc END,
      EndReason = CASE WHEN @EventType = 'SESSION_END' THEN LEFT(@Detail, 64) ELSE t.EndReason END,
      IsActive = CASE WHEN @EventType IN ('SESSION_END', 'LOGOUT') THEN 0 ELSE t.IsActive END,
      UserEmail = COALESCE(t.UserEmail, @UserEmail),
      SerialNumber = COALESCE(@SerialNumber, t.SerialNumber),
      FirstInternalIp = COALESCE(t.FirstInternalIp, @InternalIp),
      LastInternalIp = COALESCE(@InternalIp, t.LastInternalIp),
      FirstPublicIp = COALESCE(t.FirstPublicIp, @PublicIp),
      LastPublicIp = COALESCE(@PublicIp, t.LastPublicIp),
      MacAddress = COALESCE(@MacAddress, t.MacAddress),
      ExtensionVersion = COALESCE(@ExtensionVersion, t.ExtensionVersion),
      UpdatedUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN INSERT
      (SessionId, DirectoryDeviceId, SerialNumber, UserEmail, SessionStartUtc, SessionEndUtc, LoginUtc, LogoutUtc, LastSeenUtc, EndReason,
       FirstInternalIp, LastInternalIp, FirstPublicIp, LastPublicIp, MacAddress, ExtensionVersion, IsActive)
    VALUES
      (@SessionId, @DirectoryDeviceId, @SerialNumber, @UserEmail, @EventTime,
       CASE WHEN @EventType = 'SESSION_END' THEN @EventTime END,
       CASE WHEN @EventType = 'LOGIN' THEN @EventTime END,
       CASE WHEN @EventType = 'LOGOUT' THEN @EventTime END,
       @EventTime,
       CASE WHEN @EventType = 'SESSION_END' THEN LEFT(@Detail, 64) END,
       @InternalIp, @InternalIp, @PublicIp, @PublicIp, @MacAddress, @ExtensionVersion,
       CASE WHEN @EventType IN ('SESSION_END', 'LOGOUT') THEN 0 ELSE 1 END);
  END;

  INSERT dbo.ActivityEvents
    (EventId, SessionId, DirectoryDeviceId, UserEmail, EventType, EventTimeUtc, Url, Domain, Title, SearchEngine, SearchQuery,
     Transition, Detail, DownloadFileName, DownloadMime, DownloadDanger, DownloadState, InternalIp, InternalIpv6, PublicIp, MacAddress, IngestCorrelationId)
  VALUES
    (@EventId, @SessionId, @DirectoryDeviceId, @UserEmail, @EventType, @EventTime, @Url, @Domain, @Title, @SearchEngine, @SearchQuery,
     @Transition, @Detail, @DownloadFileName, @DownloadMime, @DownloadDanger, @DownloadState, @InternalIp, @InternalIpv6, @PublicIp, @MacAddress, @CorrelationId);

  IF @RecordIp = 1 AND (@InternalIp IS NOT NULL OR @PublicIp IS NOT NULL)
    INSERT dbo.IpObservations (ObservedUtc, Source, DirectoryDeviceId, UserEmail, SessionId, InternalIp, InternalIpv6, PublicIp, MacAddress)
    VALUES (@EventTime, 'EXTENSION', @DirectoryDeviceId, @UserEmail, @SessionId, @InternalIp, @InternalIpv6, @PublicIp, @MacAddress);

  SELECT CAST(1 AS int);
END;
""";

    // Updates the extension columns of the device row (Google columns are owned by the Google sync).
    public const string UpsertDeviceSql = """
MERGE dbo.Devices WITH (HOLDLOCK) AS t
USING (SELECT @DirectoryDeviceId AS DirectoryDeviceId) AS s ON t.DirectoryDeviceId = s.DirectoryDeviceId
WHEN MATCHED THEN UPDATE SET
  SerialNumber = COALESCE(t.SerialNumber, @SerialNumber),
  AssetId = COALESCE(t.AssetId, @AssetId),
  AnnotatedLocation = COALESCE(t.AnnotatedLocation, @AnnotatedLocation),
  Manufacturer = COALESCE(t.Manufacturer, @Manufacturer),
  Model = COALESCE(t.Model, @Model),
  ExtFirstSeenUtc = CASE WHEN t.ExtFirstSeenUtc IS NULL OR @EventTime < t.ExtFirstSeenUtc THEN @EventTime ELSE t.ExtFirstSeenUtc END,
  ExtLastSeenUtc = CASE WHEN t.ExtLastSeenUtc IS NULL OR @EventTime > t.ExtLastSeenUtc THEN @EventTime ELSE t.ExtLastSeenUtc END,
  ExtLastUserEmail = COALESCE(@UserEmail, t.ExtLastUserEmail),
  ExtLastInternalIp = COALESCE(@InternalIp, t.ExtLastInternalIp),
  ExtLastInternalIpv6 = COALESCE(@InternalIpv6, t.ExtLastInternalIpv6),
  ExtLastPublicIp = COALESCE(@PublicIp, t.ExtLastPublicIp),
  ExtLastMacAddress = COALESCE(@MacAddress, t.ExtLastMacAddress),
  ExtVersion = COALESCE(@ExtensionVersion, t.ExtVersion),
  ExtHostname = COALESCE(@Hostname, t.ExtHostname),
  ExtChromeVersion = COALESCE(@ChromeVersion, t.ExtChromeVersion),
  ExtPlatformVersion = COALESCE(@PlatformVersion, t.ExtPlatformVersion),
  UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
  (DirectoryDeviceId, SerialNumber, AssetId, AnnotatedLocation, Manufacturer, Model, ExtFirstSeenUtc, ExtLastSeenUtc, ExtLastUserEmail,
   ExtLastInternalIp, ExtLastInternalIpv6, ExtLastPublicIp, ExtLastMacAddress, ExtVersion, ExtHostname, ExtChromeVersion, ExtPlatformVersion)
VALUES
  (@DirectoryDeviceId, @SerialNumber, @AssetId, @AnnotatedLocation, @Manufacturer, @Model, @EventTime, @EventTime, @UserEmail,
   @InternalIp, @InternalIpv6, @PublicIp, @MacAddress, @ExtensionVersion, @Hostname, @ChromeVersion, @PlatformVersion);
""";

    public const string InsertErrorSql = """
INSERT dbo.IngestionErrors (ErrorTimeUtc, CorrelationId, Layer, ErrorCode, ErrorMessage, BlobPath)
VALUES (SYSUTCDATETIME(), @CorrelationId, @Layer, @ErrorCode, @ErrorMessage, @BlobPath);
""";

    private string? ConnectionString => configuration["SQL_CONNECTION_STRING"];

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ConnectionString) && !string.Equals(configuration["SQL_WRITE_ENABLED"], "false", StringComparison.OrdinalIgnoreCase);

    public async Task<int> WriteAsync(IReadOnlyList<ChromeEvent> events, string? publicIp, string correlationId, CancellationToken cancellationToken)
    {
        if (!IsEnabled || events.Count == 0) return 0;

        await using var sql = new SqlConnection(ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);

        var inserted = 0;
        foreach (var e in events)
        {
            await using var cmd = new SqlCommand(InsertEventSql, sql, tx);
            var eventTime = e.EventTimeUtc?.UtcDateTime ?? DateTime.UtcNow;
            var eventType = e.EventType ?? "UNKNOWN";

            Add(cmd, "@EventId", SqlDbType.UniqueIdentifier, e.EventId);
            Add(cmd, "@SessionId", SqlDbType.UniqueIdentifier, e.SessionId);
            Add(cmd, "@DirectoryDeviceId", SqlDbType.NVarChar, e.DirectoryDeviceId ?? "unknown", 128);
            Add(cmd, "@SerialNumber", SqlDbType.NVarChar, e.DeviceSerial, 128);
            Add(cmd, "@UserEmail", SqlDbType.NVarChar, e.UserEmail, 320);
            Add(cmd, "@EventType", SqlDbType.NVarChar, eventType, 32);
            Add(cmd, "@EventTime", SqlDbType.DateTime2, eventTime);
            Add(cmd, "@Url", SqlDbType.NVarChar, e.Url, 2048);
            Add(cmd, "@Domain", SqlDbType.NVarChar, e.Domain, 256);
            Add(cmd, "@Title", SqlDbType.NVarChar, e.Title, 1024);
            Add(cmd, "@SearchEngine", SqlDbType.NVarChar, e.SearchEngine, 32);
            Add(cmd, "@SearchQuery", SqlDbType.NVarChar, e.SearchQuery, 1024);
            Add(cmd, "@Transition", SqlDbType.NVarChar, e.Transition, 64);
            Add(cmd, "@Detail", SqlDbType.NVarChar, e.Detail, 1024);
            Add(cmd, "@DownloadFileName", SqlDbType.NVarChar, e.DownloadFileName, 512);
            Add(cmd, "@DownloadMime", SqlDbType.NVarChar, e.DownloadMime, 256);
            Add(cmd, "@DownloadDanger", SqlDbType.NVarChar, e.DownloadDanger, 64);
            Add(cmd, "@DownloadState", SqlDbType.NVarChar, e.DownloadState, 64);
            Add(cmd, "@InternalIp", SqlDbType.NVarChar, e.InternalIp, 64);
            Add(cmd, "@InternalIpv6", SqlDbType.NVarChar, e.InternalIpv6, 64);
            Add(cmd, "@PublicIp", SqlDbType.NVarChar, publicIp, 64);
            Add(cmd, "@MacAddress", SqlDbType.NVarChar, e.MacAddress, 64);
            Add(cmd, "@ExtensionVersion", SqlDbType.NVarChar, e.ExtensionVersion, 32);
            Add(cmd, "@CorrelationId", SqlDbType.NVarChar, correlationId, 64);
            Add(cmd, "@RecordIp", SqlDbType.Bit, IpObservationEventTypes.Contains(eventType));

            if (await cmd.ExecuteScalarAsync(cancellationToken) is int) inserted++;
        }

        // One device upsert per batch, from the newest event that identifies the device.
        var latest = events
            .Where(e => !string.IsNullOrWhiteSpace(e.DirectoryDeviceId))
            .OrderByDescending(e => e.EventTimeUtc)
            .FirstOrDefault();

        if (latest is not null)
        {
            await using var cmd = new SqlCommand(UpsertDeviceSql, sql, tx);
            Add(cmd, "@DirectoryDeviceId", SqlDbType.NVarChar, latest.DirectoryDeviceId, 128);
            Add(cmd, "@SerialNumber", SqlDbType.NVarChar, events.Select(e => e.DeviceSerial).FirstOrDefault(v => v is not null), 128);
            Add(cmd, "@AssetId", SqlDbType.NVarChar, events.Select(e => e.AssetId).FirstOrDefault(v => v is not null), 256);
            Add(cmd, "@AnnotatedLocation", SqlDbType.NVarChar, events.Select(e => e.AnnotatedLocation).FirstOrDefault(v => v is not null), 256);
            Add(cmd, "@Manufacturer", SqlDbType.NVarChar, events.Select(e => e.Manufacturer).FirstOrDefault(v => v is not null), 256);
            Add(cmd, "@Model", SqlDbType.NVarChar, events.Select(e => e.Model).FirstOrDefault(v => v is not null), 256);
            Add(cmd, "@EventTime", SqlDbType.DateTime2, latest.EventTimeUtc?.UtcDateTime ?? DateTime.UtcNow);
            Add(cmd, "@UserEmail", SqlDbType.NVarChar, latest.UserEmail, 320);
            Add(cmd, "@InternalIp", SqlDbType.NVarChar, latest.InternalIp, 64);
            Add(cmd, "@InternalIpv6", SqlDbType.NVarChar, latest.InternalIpv6, 64);
            Add(cmd, "@PublicIp", SqlDbType.NVarChar, publicIp, 64);
            Add(cmd, "@MacAddress", SqlDbType.NVarChar, latest.MacAddress, 64);
            Add(cmd, "@ExtensionVersion", SqlDbType.NVarChar, latest.ExtensionVersion, 32);
            Add(cmd, "@Hostname", SqlDbType.NVarChar, events.Select(e => e.Hostname).FirstOrDefault(v => v is not null), 256);
            Add(cmd, "@ChromeVersion", SqlDbType.NVarChar, events.Select(e => e.ChromeVersion).FirstOrDefault(v => v is not null), 64);
            Add(cmd, "@PlatformVersion", SqlDbType.NVarChar, events.Select(e => e.PlatformVersion).FirstOrDefault(v => v is not null), 64);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task LogErrorAsync(string correlationId, string layer, string code, string message, string? blobPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        try
        {
            await using var sql = new SqlConnection(ConnectionString);
            await sql.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand(InsertErrorSql, sql);
            Add(cmd, "@CorrelationId", SqlDbType.NVarChar, correlationId, 64);
            Add(cmd, "@Layer", SqlDbType.NVarChar, layer, 64);
            Add(cmd, "@ErrorCode", SqlDbType.NVarChar, code, 128);
            Add(cmd, "@ErrorMessage", SqlDbType.NVarChar, message.Length > 4000 ? message[..4000] : message, 4000);
            Add(cmd, "@BlobPath", SqlDbType.NVarChar, blobPath, 1024);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log ingestion error.");
        }
    }

    internal static void Add(SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    {
        var p = cmd.Parameters.Add(name, type);
        if (size > 0) p.Size = size;
        p.Value = value ?? DBNull.Value;
    }
}
