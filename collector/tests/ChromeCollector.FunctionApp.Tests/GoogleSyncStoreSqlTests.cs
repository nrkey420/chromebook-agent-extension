using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>Runs only when SQL_TEST_CONNECTION_STRING points at a SQL Server (CI starts one in a container).</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlTestDatabase.EnvironmentVariable)))
            Skip = $"Set {SqlTestDatabase.EnvironmentVariable} to run SQL integration tests.";
    }
}

/// <summary>Creates a throw-away database with the real schema scripts (001-003) and drops it afterwards.</summary>
public sealed class SqlTestDatabase : IAsyncLifetime
{
    public const string EnvironmentVariable = "SQL_TEST_CONNECTION_STRING";

    private readonly string _name = $"ChromeCollectorTest_{Guid.NewGuid():N}";
    private string? _server;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _server = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(_server)) return;

        await ExecAsync(_server, $"CREATE DATABASE [{_name}];");
        ConnectionString = new SqlConnectionStringBuilder(_server) { InitialCatalog = _name }.ConnectionString;

        var sqlDir = Path.Combine(RepoPaths.CollectorRoot, "src", "ChromeCollector.FunctionApp", "Sql");
        foreach (var file in new[] { "001_tables.sql", "002_views.sql", "003_procedures.sql", "004_security.sql" })
        foreach (var batch in SplitBatches(await File.ReadAllTextAsync(Path.Combine(sqlDir, file))))
            await ExecAsync(ConnectionString, batch);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        SqlConnection.ClearAllPools();
        await ExecAsync(_server, $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public Task ExecAsync(string sql) => ExecAsync(ConnectionString, sql);

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<string> SplitBatches(string script) =>
        System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b));
}

public static class RepoPaths
{
    public static string CollectorRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ChromeCollector.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Run tests inside the collector folder.");
        }
    }
}

public class GoogleSyncStoreSqlTests(SqlTestDatabase db) : IClassFixture<SqlTestDatabase>
{
    private static readonly DateTime T0 = new(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc);

    private GoogleSyncStore Store => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["SQL_CONNECTION_STRING"] = db.ConnectionString }).Build());

    private static GoogleDevice Device(string id, DateTime lastSync, string lan, params string[] recentUsers) => new()
    {
        DirectoryDeviceId = id,
        SerialNumber = $"SN-{id}",
        AssetId = $"A-{id}",
        AnnotatedLocation = "Room 204",
        OrgUnitPath = "/Students/HS",
        Model = "Acer Chromebook 311",
        GoogleStatus = "ACTIVE",
        MacAddress = "aabbccddeeff",
        GoogleLastSyncUtc = lastSync,
        GoogleLastLanIp = lan,
        GoogleLastWanIp = "203.0.113.9",
        RecentUsers = recentUsers.Select((u, i) => new GoogleRecentUser(i, u, "USER_TYPE_MANAGED")).ToList(),
        ActiveDays = [new GoogleActiveDay(DateOnly.FromDateTime(lastSync), 60)],
    };

    [SqlFact]
    public async Task Devices_UpsertGoogleColumns_KeepExtensionColumns_ReplaceRecentUsers_RecordIpOncePerCheckIn()
    {
        // The extension saw the device first.
        await db.ExecAsync("""
            INSERT dbo.Devices (DirectoryDeviceId, SerialNumber, ExtFirstSeenUtc, ExtLastSeenUtc, ExtLastUserEmail, ExtVersion)
            VALUES ('dev-1', 'SN-dev-1', '2026-09-20', '2026-09-28', 'ext@district.org', '0.3.0');
            """);

        (await Store.UpsertDevicesAsync([Device("dev-1", T0, "10.1.2.3", "a@district.org", "b@district.org"), Device("dev-2", T0, "10.1.2.4")], T0, default))
            .Should().Be(2);

        (await db.ScalarAsync("SELECT CONCAT(AssetId, '|', OrgUnitPath, '|', GoogleStatus, '|', ExtVersion, '|', ExtLastUserEmail) FROM dbo.Devices WHERE DirectoryDeviceId = 'dev-1'"))
            .Should().Be("A-dev-1|/Students/HS|ACTIVE|0.3.0|ext@district.org");
        (await db.ScalarAsync("SELECT COUNT(*) FROM dbo.GoogleDeviceRecentUsers WHERE DirectoryDeviceId = 'dev-1'")).Should().Be(2);
        (await db.ScalarAsync("SELECT COUNT(*) FROM dbo.IpObservations WHERE Source = 'GOOGLE_SYNC' AND DirectoryDeviceId IN ('dev-1', 'dev-2')")).Should().Be(2);
        // Google checked in after the extension went quiet for more than a day.
        (await db.ScalarAsync("SELECT ExtensionReportingStatus FROM dbo.vw_Devices WHERE DirectoryDeviceId = 'dev-1'")).Should().Be("EXTENSION_SILENT");
        (await db.ScalarAsync("SELECT ExtensionReportingStatus FROM dbo.vw_Devices WHERE DirectoryDeviceId = 'dev-2'")).Should().Be("NEVER_REPORTED");

        // Same check-in again: no new IP observation; recent users replaced; active minutes updated.
        var again = Device("dev-1", T0, "10.1.2.3", "c@district.org") with { ActiveDays = [new GoogleActiveDay(DateOnly.FromDateTime(T0), 75)] };
        await Store.UpsertDevicesAsync([again], T0.AddHours(6), default);
        (await db.ScalarAsync("SELECT STRING_AGG(UserEmail, ',') FROM dbo.GoogleDeviceRecentUsers WHERE DirectoryDeviceId = 'dev-1'")).Should().Be("c@district.org");
        (await db.ScalarAsync("SELECT ActiveMinutes FROM dbo.GoogleDeviceActiveTime WHERE DirectoryDeviceId = 'dev-1'")).Should().Be(75);
        (await db.ScalarAsync("SELECT COUNT(*) FROM dbo.IpObservations WHERE Source = 'GOOGLE_SYNC' AND DirectoryDeviceId = 'dev-1'")).Should().Be(1);

        // A newer check-in records a new observation.
        await Store.UpsertDevicesAsync([Device("dev-1", T0.AddHours(3), "10.9.9.9")], T0.AddHours(6), default);
        (await db.ScalarAsync("SELECT COUNT(*) FROM dbo.IpObservations WHERE Source = 'GOOGLE_SYNC' AND DirectoryDeviceId = 'dev-1'")).Should().Be(2);
        (await db.ScalarAsync("SELECT GoogleLastLanIp FROM dbo.Devices WHERE DirectoryDeviceId = 'dev-1'")).Should().Be("10.9.9.9");
    }

    [SqlFact]
    public async Task Users_InsertThenUpdate()
    {
        var user = new GoogleUserRecord { UserEmail = "123456@district.org", GoogleUserId = "g1", OrgUnitPath = "/Students", StudentId = "S-1", IsSuspended = false };
        await Store.UpsertUsersAsync([user, user], T0, default);
        await Store.UpsertUsersAsync([user with { StudentId = "S-2", IsSuspended = true }], T0.AddDays(1), default);

        (await db.ScalarAsync("SELECT COUNT(*) FROM dbo.GoogleUsers WHERE UserEmail = '123456@district.org'")).Should().Be(1);
        (await db.ScalarAsync("SELECT CONCAT(StudentId, '|', IsSuspended) FROM dbo.GoogleUsers WHERE UserEmail = '123456@district.org'")).Should().Be("S-2|1");
    }

    [SqlFact]
    public async Task AuditEvents_SkipDuplicates_AndFeedLoginAndIpViews()
    {
        await Store.UpsertDevicesAsync([Device("dev-9", T0.AddMinutes(30), "10.5.5.5")], T0, default);
        await Store.UpsertUsersAsync([new GoogleUserRecord { UserEmail = "student9@district.org", StudentId = "S-9" }], T0, default);

        var login = new GoogleAuditEvent
        {
            Application = "chrome", UniqueQualifier = "111", EventTimeUtc = T0, EventName = "CHROME_OS_LOGIN_EVENT",
            EventType = "CHROME_OS_LOGIN_LOGOUT_TYPE", UserEmail = "student9@district.org", DirectoryDeviceId = "dev-9",
        };
        var failure = login with { UniqueQualifier = "112", EventName = "CHROME_OS_LOGIN_FAILURE_EVENT", FailureReason = "AUTHENTICATION_ERROR" };

        (await Store.InsertAuditEventsAsync([login, failure, login], default)).Should().Be(2);
        (await Store.InsertAuditEventsAsync([login, failure], default)).Should().Be(0); // overlapping run
        (await Store.InsertAuditEventsAsync([login with { UniqueQualifier = "113", EventTimeUtc = T0.AddHours(1) }], default)).Should().Be(1);

        (await db.ScalarAsync("""
            SELECT CONCAT(Source, '|', LoginEvent, '|', SerialNumber, '|', StudentId) FROM dbo.vw_LoginHistory
            WHERE DirectoryDeviceId = 'dev-9' AND EventTimeUtc = '2026-10-01T13:00:00' AND LoginEvent = 'LOGIN'
            """)).Should().Be("GOOGLE_CHROMEOS|LOGIN|SN-dev-9|S-9");

        // Google's IP observation (13:30) is attributed to the user who logged in at 13:00.
        (await db.ScalarAsync("SELECT CONCAT(UserEmail, '|', UserSource) FROM dbo.vw_IpHistory WHERE DirectoryDeviceId = 'dev-9'"))
            .Should().Be("student9@district.org|INFERRED_FROM_GOOGLE_LOGIN");
    }

    [SqlFact]
    public async Task SyncState_SuccessMovesWatermark_FailureKeepsIt()
    {
        (await Store.GetWatermarkAsync("GoogleAudit:test", default)).Should().BeNull();

        await Store.RecordRunAsync("GoogleAudit:test", T0, "SUCCESS", null, 5, T0, default);
        await Store.RecordRunAsync("GoogleAudit:test", T0.AddMinutes(15), "FAILED", new string('x', 3000), 0, null, default);

        (await Store.GetWatermarkAsync("GoogleAudit:test", default)).Should().Be(T0);
        (await db.ScalarAsync("SELECT CONCAT(LastStatus, '|', LEN(LastMessage)) FROM dbo.SyncState WHERE SyncName = 'GoogleAudit:test'")).Should().Be("FAILED|2000");
    }
}
