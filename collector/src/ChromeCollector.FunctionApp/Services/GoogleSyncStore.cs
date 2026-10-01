using System.Data;
using ChromeCollector.FunctionApp.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace ChromeCollector.FunctionApp.Services;

public interface IGoogleSyncStore
{
    bool IsEnabled { get; }
    Task<int> UpsertDevicesAsync(IReadOnlyList<GoogleDevice> devices, DateTime refreshedUtc, CancellationToken cancellationToken);
    Task<int> UpsertUsersAsync(IReadOnlyList<GoogleUserRecord> users, DateTime refreshedUtc, CancellationToken cancellationToken);
    /// <summary>Returns the number of new rows (events already stored are skipped).</summary>
    Task<int> InsertAuditEventsAsync(IReadOnlyList<GoogleAuditEvent> events, CancellationToken cancellationToken);
    Task<DateTime?> GetWatermarkAsync(string syncName, CancellationToken cancellationToken);
    Task RecordRunAsync(string syncName, DateTime runUtc, string status, string? message, int itemsProcessed, DateTime? watermarkUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Writes one Google page per transaction: bulk copy into temp tables, then set-based statements.
/// Google owns the Google columns of dbo.Devices; the extension's Ext* columns are never touched here.
/// </summary>
public sealed class GoogleSyncStore(IConfiguration configuration) : IGoogleSyncStore
{
    public const string IpObservationSource = "GOOGLE_SYNC";

    public const string CreateDeviceStagingSql = """
DROP TABLE IF EXISTS #GoogleDevices;
DROP TABLE IF EXISTS #GoogleRecentUsers;
DROP TABLE IF EXISTS #GoogleActiveTime;
CREATE TABLE #GoogleDevices (
  DirectoryDeviceId nvarchar(128) NOT NULL PRIMARY KEY,
  SerialNumber nvarchar(128) NULL,
  AssetId nvarchar(256) NULL,
  AnnotatedLocation nvarchar(256) NULL,
  AnnotatedUser nvarchar(256) NULL,
  Notes nvarchar(1000) NULL,
  OrgUnitPath nvarchar(512) NULL,
  Model nvarchar(256) NULL,
  OsVersion nvarchar(64) NULL,
  PlatformVersion nvarchar(128) NULL,
  FirmwareVersion nvarchar(128) NULL,
  BootMode nvarchar(32) NULL,
  GoogleStatus nvarchar(32) NULL,
  MacAddress nvarchar(64) NULL,
  EthernetMacAddress nvarchar(64) NULL,
  AutoUpdateThrough nvarchar(32) NULL,
  FirstEnrollmentUtc datetime2 NULL,
  LastEnrollmentUtc datetime2 NULL,
  GoogleLastSyncUtc datetime2 NULL,
  GoogleLastLanIp nvarchar(64) NULL,
  GoogleLastWanIp nvarchar(64) NULL,
  GoogleRefreshedUtc datetime2 NOT NULL
);
CREATE TABLE #GoogleRecentUsers (
  DirectoryDeviceId nvarchar(128) NOT NULL,
  Position int NOT NULL,
  UserEmail nvarchar(320) NULL,
  UserType nvarchar(32) NULL,
  RefreshedUtc datetime2 NOT NULL,
  PRIMARY KEY (DirectoryDeviceId, Position)
);
CREATE TABLE #GoogleActiveTime (
  DirectoryDeviceId nvarchar(128) NOT NULL,
  ActiveDate date NOT NULL,
  ActiveMinutes int NOT NULL,
  RefreshedUtc datetime2 NOT NULL,
  PRIMARY KEY (DirectoryDeviceId, ActiveDate)
);
""";

    // Order matters: the IP observation compares against the device row before the MERGE updates it.
    public const string MergeDevicesSql = """
-- Google's last known LAN/WAN IP, recorded once per Google check-in (new GoogleLastSyncUtc).
INSERT dbo.IpObservations (ObservedUtc, Source, DirectoryDeviceId, UserEmail, SessionId, InternalIp, InternalIpv6, PublicIp, MacAddress)
SELECT s.GoogleLastSyncUtc, @IpSource, s.DirectoryDeviceId, NULL, NULL, s.GoogleLastLanIp, NULL, s.GoogleLastWanIp, s.MacAddress
FROM #GoogleDevices s
LEFT JOIN dbo.Devices d ON d.DirectoryDeviceId = s.DirectoryDeviceId
WHERE s.GoogleLastSyncUtc IS NOT NULL
  AND (s.GoogleLastLanIp IS NOT NULL OR s.GoogleLastWanIp IS NOT NULL)
  AND (d.GoogleLastSyncUtc IS NULL OR s.GoogleLastSyncUtc > d.GoogleLastSyncUtc);

MERGE dbo.Devices WITH (HOLDLOCK) AS t
USING #GoogleDevices AS s ON t.DirectoryDeviceId = s.DirectoryDeviceId
WHEN MATCHED THEN UPDATE SET
  SerialNumber = COALESCE(s.SerialNumber, t.SerialNumber),
  AssetId = s.AssetId,
  AnnotatedLocation = s.AnnotatedLocation,
  AnnotatedUser = s.AnnotatedUser,
  Notes = s.Notes,
  OrgUnitPath = s.OrgUnitPath,
  Model = COALESCE(s.Model, t.Model),
  OsVersion = s.OsVersion,
  PlatformVersion = s.PlatformVersion,
  FirmwareVersion = s.FirmwareVersion,
  BootMode = s.BootMode,
  GoogleStatus = s.GoogleStatus,
  MacAddress = s.MacAddress,
  EthernetMacAddress = s.EthernetMacAddress,
  AutoUpdateThrough = s.AutoUpdateThrough,
  FirstEnrollmentUtc = s.FirstEnrollmentUtc,
  LastEnrollmentUtc = s.LastEnrollmentUtc,
  GoogleLastSyncUtc = COALESCE(s.GoogleLastSyncUtc, t.GoogleLastSyncUtc),
  GoogleLastLanIp = COALESCE(s.GoogleLastLanIp, t.GoogleLastLanIp),
  GoogleLastWanIp = COALESCE(s.GoogleLastWanIp, t.GoogleLastWanIp),
  GoogleRefreshedUtc = s.GoogleRefreshedUtc,
  UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
  (DirectoryDeviceId, SerialNumber, AssetId, AnnotatedLocation, AnnotatedUser, Notes, OrgUnitPath, Model, OsVersion, PlatformVersion,
   FirmwareVersion, BootMode, GoogleStatus, MacAddress, EthernetMacAddress, AutoUpdateThrough, FirstEnrollmentUtc, LastEnrollmentUtc,
   GoogleLastSyncUtc, GoogleLastLanIp, GoogleLastWanIp, GoogleRefreshedUtc)
VALUES
  (s.DirectoryDeviceId, s.SerialNumber, s.AssetId, s.AnnotatedLocation, s.AnnotatedUser, s.Notes, s.OrgUnitPath, s.Model, s.OsVersion, s.PlatformVersion,
   s.FirmwareVersion, s.BootMode, s.GoogleStatus, s.MacAddress, s.EthernetMacAddress, s.AutoUpdateThrough, s.FirstEnrollmentUtc, s.LastEnrollmentUtc,
   s.GoogleLastSyncUtc, s.GoogleLastLanIp, s.GoogleLastWanIp, s.GoogleRefreshedUtc);

-- The recent-users list is replaced as a whole for every device in the page.
DELETE r FROM dbo.GoogleDeviceRecentUsers r
WHERE EXISTS (SELECT 1 FROM #GoogleDevices s WHERE s.DirectoryDeviceId = r.DirectoryDeviceId);

INSERT dbo.GoogleDeviceRecentUsers (DirectoryDeviceId, Position, UserEmail, UserType, RefreshedUtc)
SELECT DirectoryDeviceId, Position, UserEmail, UserType, RefreshedUtc FROM #GoogleRecentUsers;

MERGE dbo.GoogleDeviceActiveTime WITH (HOLDLOCK) AS t
USING #GoogleActiveTime AS s ON t.DirectoryDeviceId = s.DirectoryDeviceId AND t.ActiveDate = s.ActiveDate
WHEN MATCHED THEN UPDATE SET ActiveMinutes = s.ActiveMinutes, RefreshedUtc = s.RefreshedUtc
WHEN NOT MATCHED THEN INSERT (DirectoryDeviceId, ActiveDate, ActiveMinutes, RefreshedUtc)
VALUES (s.DirectoryDeviceId, s.ActiveDate, s.ActiveMinutes, s.RefreshedUtc);
""";

    public const string CreateUserStagingSql = """
DROP TABLE IF EXISTS #GoogleUsers;
CREATE TABLE #GoogleUsers (
  UserEmail nvarchar(320) NOT NULL PRIMARY KEY,
  GoogleUserId nvarchar(64) NULL,
  OrgUnitPath nvarchar(512) NULL,
  StudentId nvarchar(128) NULL,
  IsSuspended bit NULL,
  IsArchived bit NULL,
  IsAdmin bit NULL,
  LastLoginUtc datetime2 NULL,
  CreatedInGoogleUtc datetime2 NULL,
  RefreshedUtc datetime2 NOT NULL
);
""";

    public const string MergeUsersSql = """
MERGE dbo.GoogleUsers WITH (HOLDLOCK) AS t
USING #GoogleUsers AS s ON t.UserEmail = s.UserEmail
WHEN MATCHED THEN UPDATE SET
  GoogleUserId = s.GoogleUserId,
  OrgUnitPath = s.OrgUnitPath,
  StudentId = s.StudentId,
  IsSuspended = s.IsSuspended,
  IsArchived = s.IsArchived,
  IsAdmin = s.IsAdmin,
  LastLoginUtc = s.LastLoginUtc,
  CreatedInGoogleUtc = s.CreatedInGoogleUtc,
  RefreshedUtc = s.RefreshedUtc
WHEN NOT MATCHED THEN INSERT
  (UserEmail, GoogleUserId, OrgUnitPath, StudentId, IsSuspended, IsArchived, IsAdmin, LastLoginUtc, CreatedInGoogleUtc, RefreshedUtc)
VALUES
  (s.UserEmail, s.GoogleUserId, s.OrgUnitPath, s.StudentId, s.IsSuspended, s.IsArchived, s.IsAdmin, s.LastLoginUtc, s.CreatedInGoogleUtc, s.RefreshedUtc);
""";

    public const string CreateAuditStagingSql = """
DROP TABLE IF EXISTS #GoogleAudit;
CREATE TABLE #GoogleAudit (
  Application nvarchar(32) NOT NULL,
  UniqueQualifier nvarchar(64) NOT NULL,
  EventTimeUtc datetime2 NOT NULL,
  EventType nvarchar(128) NULL,
  EventName nvarchar(128) NOT NULL,
  UserEmail nvarchar(320) NULL,
  ActorEmail nvarchar(320) NULL,
  IpAddress nvarchar(64) NULL,
  DirectoryDeviceId nvarchar(128) NULL,
  DeviceName nvarchar(256) NULL,
  DevicePlatform nvarchar(128) NULL,
  EventReason nvarchar(256) NULL,
  FailureReason nvarchar(256) NULL,
  ParametersJson nvarchar(max) NULL
);
""";

    // Audit runs overlap on purpose (late events); rows already stored are skipped.
    public const string InsertAuditSql = """
INSERT dbo.GoogleAuditEvents
  (Application, UniqueQualifier, EventTimeUtc, EventType, EventName, UserEmail, ActorEmail, IpAddress,
   DirectoryDeviceId, DeviceName, DevicePlatform, EventReason, FailureReason, ParametersJson)
SELECT s.Application, s.UniqueQualifier, s.EventTimeUtc, s.EventType, s.EventName, s.UserEmail, s.ActorEmail, s.IpAddress,
       s.DirectoryDeviceId, s.DeviceName, s.DevicePlatform, s.EventReason, s.FailureReason, s.ParametersJson
FROM #GoogleAudit s
WHERE NOT EXISTS (
  SELECT 1 FROM dbo.GoogleAuditEvents e
  WHERE e.Application = s.Application AND e.UniqueQualifier = s.UniqueQualifier
    AND e.EventName = s.EventName AND e.EventTimeUtc = s.EventTimeUtc);
SELECT @@ROWCOUNT;
""";

    public const string GetWatermarkSql = "SELECT WatermarkUtc FROM dbo.SyncState WHERE SyncName = @SyncName;";

    public const string RecordRunSql = """
MERGE dbo.SyncState WITH (HOLDLOCK) AS t
USING (SELECT @SyncName AS SyncName) AS s ON t.SyncName = s.SyncName
WHEN MATCHED THEN UPDATE SET
  WatermarkUtc = COALESCE(@WatermarkUtc, t.WatermarkUtc),
  LastRunUtc = @RunUtc,
  LastStatus = @Status,
  LastMessage = @Message,
  ItemsProcessed = @Items
WHEN NOT MATCHED THEN INSERT (SyncName, WatermarkUtc, LastRunUtc, LastStatus, LastMessage, ItemsProcessed)
VALUES (@SyncName, @WatermarkUtc, @RunUtc, @Status, @Message, @Items);
""";

    private string? ConnectionString => configuration["SQL_CONNECTION_STRING"];

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public async Task<int> UpsertDevicesAsync(IReadOnlyList<GoogleDevice> devices, DateTime refreshedUtc, CancellationToken cancellationToken)
    {
        // A page could repeat a device; MERGE fails on duplicate source keys.
        var unique = devices.GroupBy(d => d.DirectoryDeviceId, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        if (unique.Count == 0) return 0;

        var deviceTable = DeviceTable(unique, refreshedUtc);
        var recentTable = RecentUserTable(unique, refreshedUtc);
        var activeTable = ActiveTimeTable(unique, refreshedUtc);

        await RunStagedAsync(CreateDeviceStagingSql,
            [("#GoogleDevices", deviceTable), ("#GoogleRecentUsers", recentTable), ("#GoogleActiveTime", activeTable)],
            MergeDevicesSql, cmd => SqlWriter.Add(cmd, "@IpSource", SqlDbType.NVarChar, IpObservationSource, 32), cancellationToken);
        return unique.Count;
    }

    public async Task<int> UpsertUsersAsync(IReadOnlyList<GoogleUserRecord> users, DateTime refreshedUtc, CancellationToken cancellationToken)
    {
        var unique = users.GroupBy(u => u.UserEmail, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        if (unique.Count == 0) return 0;

        var table = Table(
            ("UserEmail", typeof(string)), ("GoogleUserId", typeof(string)), ("OrgUnitPath", typeof(string)), ("StudentId", typeof(string)),
            ("IsSuspended", typeof(bool)), ("IsArchived", typeof(bool)), ("IsAdmin", typeof(bool)),
            ("LastLoginUtc", typeof(DateTime)), ("CreatedInGoogleUtc", typeof(DateTime)), ("RefreshedUtc", typeof(DateTime)));
        foreach (var u in unique)
            table.Rows.Add(u.UserEmail, Db(u.GoogleUserId), Db(u.OrgUnitPath), Db(u.StudentId), Db(u.IsSuspended), Db(u.IsArchived), Db(u.IsAdmin),
                Db(u.LastLoginUtc), Db(u.CreatedInGoogleUtc), refreshedUtc);

        await RunStagedAsync(CreateUserStagingSql, [("#GoogleUsers", table)], MergeUsersSql, null, cancellationToken);
        return unique.Count;
    }

    public async Task<int> InsertAuditEventsAsync(IReadOnlyList<GoogleAuditEvent> events, CancellationToken cancellationToken)
    {
        var unique = events.DistinctBy(e => (e.Application, e.UniqueQualifier, e.EventName, e.EventTimeUtc)).ToList();
        if (unique.Count == 0) return 0;

        var table = Table(
            ("Application", typeof(string)), ("UniqueQualifier", typeof(string)), ("EventTimeUtc", typeof(DateTime)), ("EventType", typeof(string)),
            ("EventName", typeof(string)), ("UserEmail", typeof(string)), ("ActorEmail", typeof(string)), ("IpAddress", typeof(string)),
            ("DirectoryDeviceId", typeof(string)), ("DeviceName", typeof(string)), ("DevicePlatform", typeof(string)),
            ("EventReason", typeof(string)), ("FailureReason", typeof(string)), ("ParametersJson", typeof(string)));
        foreach (var e in unique)
            table.Rows.Add(e.Application, e.UniqueQualifier, e.EventTimeUtc, Db(e.EventType), e.EventName, Db(e.UserEmail), Db(e.ActorEmail),
                Db(e.IpAddress), Db(e.DirectoryDeviceId), Db(e.DeviceName), Db(e.DevicePlatform), Db(e.EventReason), Db(e.FailureReason),
                Db(e.ParametersJson));

        var inserted = await RunStagedAsync(CreateAuditStagingSql, [("#GoogleAudit", table)], InsertAuditSql, null, cancellationToken);
        return inserted ?? 0;
    }

    public async Task<DateTime?> GetWatermarkAsync(string syncName, CancellationToken cancellationToken)
    {
        await using var sql = new SqlConnection(ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(GetWatermarkSql, sql);
        SqlWriter.Add(cmd, "@SyncName", SqlDbType.NVarChar, syncName, 64);
        return await cmd.ExecuteScalarAsync(cancellationToken) is DateTime value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }

    public async Task RecordRunAsync(string syncName, DateTime runUtc, string status, string? message, int itemsProcessed, DateTime? watermarkUtc, CancellationToken cancellationToken)
    {
        await using var sql = new SqlConnection(ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(RecordRunSql, sql);
        SqlWriter.Add(cmd, "@SyncName", SqlDbType.NVarChar, syncName, 64);
        SqlWriter.Add(cmd, "@RunUtc", SqlDbType.DateTime2, runUtc);
        SqlWriter.Add(cmd, "@Status", SqlDbType.NVarChar, status, 16);
        SqlWriter.Add(cmd, "@Message", SqlDbType.NVarChar, message is { Length: > 2000 } ? message[..2000] : message, 2000);
        SqlWriter.Add(cmd, "@Items", SqlDbType.Int, itemsProcessed);
        SqlWriter.Add(cmd, "@WatermarkUtc", SqlDbType.DateTime2, watermarkUtc);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Creates the temp tables, bulk copies the rows, runs the apply statement; all in one transaction.</summary>
    private async Task<int?> RunStagedAsync(string createSql, (string Table, DataTable Rows)[] stages, string applySql,
        Action<SqlCommand>? addParameters, CancellationToken cancellationToken)
    {
        await using var sql = new SqlConnection(ConnectionString);
        await sql.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await sql.BeginTransactionAsync(cancellationToken);

        await using (var create = new SqlCommand(createSql, sql, tx))
            await create.ExecuteNonQueryAsync(cancellationToken);

        foreach (var (tableName, rows) in stages)
        {
            if (rows.Rows.Count == 0) continue;
            using var bulk = new SqlBulkCopy(sql, SqlBulkCopyOptions.Default, tx) { DestinationTableName = tableName, BulkCopyTimeout = 120 };
            foreach (DataColumn column in rows.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(rows, cancellationToken);
        }

        int? result;
        await using (var apply = new SqlCommand(applySql, sql, tx) { CommandTimeout = 120 })
        {
            addParameters?.Invoke(apply);
            result = await apply.ExecuteScalarAsync(cancellationToken) is int count ? count : null;
        }

        await tx.CommitAsync(cancellationToken);
        return result;
    }

    public static DataTable DeviceTable(IEnumerable<GoogleDevice> devices, DateTime refreshedUtc)
    {
        var table = Table(
            ("DirectoryDeviceId", typeof(string)), ("SerialNumber", typeof(string)), ("AssetId", typeof(string)), ("AnnotatedLocation", typeof(string)),
            ("AnnotatedUser", typeof(string)), ("Notes", typeof(string)), ("OrgUnitPath", typeof(string)), ("Model", typeof(string)),
            ("OsVersion", typeof(string)), ("PlatformVersion", typeof(string)), ("FirmwareVersion", typeof(string)), ("BootMode", typeof(string)),
            ("GoogleStatus", typeof(string)), ("MacAddress", typeof(string)), ("EthernetMacAddress", typeof(string)), ("AutoUpdateThrough", typeof(string)),
            ("FirstEnrollmentUtc", typeof(DateTime)), ("LastEnrollmentUtc", typeof(DateTime)), ("GoogleLastSyncUtc", typeof(DateTime)),
            ("GoogleLastLanIp", typeof(string)), ("GoogleLastWanIp", typeof(string)), ("GoogleRefreshedUtc", typeof(DateTime)));
        foreach (var d in devices)
            table.Rows.Add(d.DirectoryDeviceId, Db(d.SerialNumber), Db(d.AssetId), Db(d.AnnotatedLocation), Db(d.AnnotatedUser), Db(d.Notes),
                Db(d.OrgUnitPath), Db(d.Model), Db(d.OsVersion), Db(d.PlatformVersion), Db(d.FirmwareVersion), Db(d.BootMode), Db(d.GoogleStatus),
                Db(d.MacAddress), Db(d.EthernetMacAddress), Db(d.AutoUpdateThrough), Db(d.FirstEnrollmentUtc), Db(d.LastEnrollmentUtc),
                Db(d.GoogleLastSyncUtc), Db(d.GoogleLastLanIp), Db(d.GoogleLastWanIp), refreshedUtc);
        return table;
    }

    public static DataTable RecentUserTable(IEnumerable<GoogleDevice> devices, DateTime refreshedUtc)
    {
        var table = Table(("DirectoryDeviceId", typeof(string)), ("Position", typeof(int)), ("UserEmail", typeof(string)),
            ("UserType", typeof(string)), ("RefreshedUtc", typeof(DateTime)));
        foreach (var d in devices)
        foreach (var u in d.RecentUsers)
            table.Rows.Add(d.DirectoryDeviceId, u.Position, Db(u.UserEmail), Db(u.UserType), refreshedUtc);
        return table;
    }

    public static DataTable ActiveTimeTable(IEnumerable<GoogleDevice> devices, DateTime refreshedUtc)
    {
        var table = Table(("DirectoryDeviceId", typeof(string)), ("ActiveDate", typeof(DateTime)), ("ActiveMinutes", typeof(int)),
            ("RefreshedUtc", typeof(DateTime)));
        foreach (var d in devices)
        foreach (var a in d.ActiveDays)
            table.Rows.Add(d.DirectoryDeviceId, a.Date.ToDateTime(TimeOnly.MinValue), a.ActiveMinutes, refreshedUtc);
        return table;
    }

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns) table.Columns.Add(name, type);
        return table;
    }

    private static object Db(object? value) => value ?? DBNull.Value;
}
