using System.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>Runs the investigation procedures from 003_procedures.sql against SQL Server.</summary>
public class InvestigationProcedureSqlTests(SqlTestDatabase db) : IClassFixture<SqlTestDatabase>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(db.ConnectionString)) return;
        await db.ExecAsync("""
            DELETE dbo.ActivityEvents; DELETE dbo.Devices; DELETE dbo.GoogleUsers; DELETE dbo.InvestigationAudit;
            UPDATE dbo.ReportingSettings SET SettingValue = 'Eastern Standard Time' WHERE SettingName = 'ReportingTimeZone';

            INSERT dbo.Devices (DirectoryDeviceId, SerialNumber, AssetId, AnnotatedLocation)
            VALUES ('dev-1', '5CD1234XYZ', 'A-100', 'Room 204'), ('dev-2', '5CD9999AAA', 'A-200', 'Library');
            INSERT dbo.GoogleUsers (UserEmail, StudentId, RefreshedUtc)
            VALUES ('123456@district.org', 'S-123456', SYSUTCDATETIME()), ('777777@district.org', 'S-777777', SYSUTCDATETIME());

            INSERT dbo.ActivityEvents (EventId, DirectoryDeviceId, UserEmail, EventType, EventTimeUtc, Url, Domain, Title, SearchEngine, SearchQuery, DownloadFileName, DownloadDanger)
            VALUES
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:00:00', 'https://www.google.com/search?q=homework', 'google.com', 'homework - Google Search', 'Google', 'homework', NULL, NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:05:00', 'https://example.com/', 'example.com', 'Example', NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:06:00', 'https://mail.example.com/', 'mail.example.com', 'Mail', NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:07:00', 'https://notexample.com/', 'notexample.com', 'Not it', NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'DOWNLOAD',   '2026-10-01T13:10:00', 'https://example.com/a.exe', 'example.com', NULL, NULL, NULL, 'a.exe', 'dangerous_file'),
              (NEWID(), 'dev-1', '123456@district.org', 'HEARTBEAT',  '2026-10-01T13:15:00', NULL, NULL, NULL, NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T15:00:00', 'https://late.example.org/', 'late.example.org', 'Outside window', NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-2', '123456@district.org', 'NAVIGATION', '2026-10-01T13:20:00', 'https://library.example.org/', 'library.example.org', 'Other device', NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-1', '777777@district.org', 'NAVIGATION', '2026-10-01T13:30:00', 'https://games.example.net/', 'games.example.net', 'Other user', NULL, NULL, NULL, NULL);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string From = "2026-10-01T12:00:00";
    private const string To = "2026-10-01T14:00:00";

    [SqlFact]
    public async Task ByStudentId_ReturnsVisitsSearchesAndDownloadsOnAllDevices_OldestFirst_NoHeartbeats()
    {
        var (rows, summary) = await WebActivity(("@User", "S-123456"), ("@From", From), ("@To", To), ("@TimesAreUtc", true), ("@CaseNumber", "IR-1"));

        rows.Select(r => r["Domain"]).Should().Equal("google.com", "example.com", "mail.example.com", "notexample.com", "example.com", "library.example.org");
        rows.Select(r => r["EventType"]).Should().NotContain("HEARTBEAT");
        rows[0]["SearchQuery"].Should().Be("homework");
        rows[0]["StudentId"].Should().Be("S-123456");
        rows[0]["SerialNumber"].Should().Be("5CD1234XYZ");
        rows[0]["Truncated"].Should().Be(false);
        rows[0]["CaseNumber"].Should().Be("IR-1");
        // 13:00 UTC is 09:00 Eastern daylight time.
        rows[0]["EventTimeLocal"].Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));

        var example = summary.Single(s => (string)s["Domain"] == "example.com");
        example["Visits"].Should().Be(1);
        example["Downloads"].Should().Be(1);
        summary.Single(s => (string)s["Domain"] == "google.com")["Searches"].Should().Be(1);
    }

    [SqlFact]
    public async Task BySerialAndUser_IsThatUserOnThatDevice()
    {
        var (rows, _) = await WebActivity(("@Device", "5CD1234XYZ"), ("@User", "123456@District.org"), ("@From", From), ("@To", To),
            ("@TimesAreUtc", true), ("@CaseNumber", "IR-2"));

        rows.Should().HaveCount(5);
        rows.Select(r => r["DirectoryDeviceId"]).Should().AllBeEquivalentTo("dev-1");
        rows.Select(r => r["UserEmail"]).Should().AllBeEquivalentTo("123456@district.org");
    }

    [SqlFact]
    public async Task ByAssetTag_ReturnsEveryUserOnTheDevice()
    {
        var (rows, _) = await WebActivity(("@Device", "A-100"), ("@From", From), ("@To", To), ("@TimesAreUtc", true), ("@CaseNumber", "IR-3"));

        rows.Select(r => r["UserEmail"]).Distinct().Should().BeEquivalentTo(["123456@district.org", "777777@district.org"]);
    }

    [SqlFact]
    public async Task DomainFilter_MatchesSubdomainsButNotLookalikes_AndDownloadsCanBeExcluded()
    {
        var (rows, _) = await WebActivity(("@User", "S-123456"), ("@From", From), ("@To", To), ("@TimesAreUtc", true),
            ("@Domain", "WWW.Example.com"), ("@IncludeDownloads", false), ("@CaseNumber", "IR-4"));

        rows.Select(r => r["Domain"]).Should().Equal("example.com", "mail.example.com");
    }

    [SqlFact]
    public async Task LocalTimes_AreConvertedToUtc()
    {
        // 09:00-10:00 Eastern daylight = 13:00-14:00 UTC.
        var (rows, _) = await WebActivity(("@User", "S-123456"), ("@From", "2026-10-01T09:00:00"), ("@To", "2026-10-01T10:00:00"), ("@CaseNumber", "IR-5"));

        rows.Should().HaveCount(6);
    }

    [SqlFact]
    public async Task MaxRows_TruncatesAndFlagsIt_AuditRecordsWhoWhatAndRowCount()
    {
        var (rows, summary) = await WebActivity(("@User", "S-123456"), ("@From", From), ("@To", To), ("@TimesAreUtc", true),
            ("@MaxRows", 2), ("@CaseNumber", " IR-6 "));

        rows.Should().HaveCount(2);
        rows.Select(r => r["Truncated"]).Should().AllBeEquivalentTo(true);
        summary.Sum(s => (int)s["Visits"] + (int)s["Downloads"]).Should().Be(6, "the summary covers everything that matched");

        (await db.ScalarAsync("""
            SELECT CONCAT(CaseNumber, '|', ProcedureName, '|', RowsReturned, '|', CASE WHEN RunBy = ORIGINAL_LOGIN() THEN 'me' END)
            FROM dbo.InvestigationAudit WHERE CaseNumber = 'IR-6'
            """)).Should().Be("IR-6|usp_WebActivity|2|me");
        ((string)(await db.ScalarAsync("SELECT Parameters FROM dbo.InvestigationAudit WHERE CaseNumber = 'IR-6'"))!)
            .Should().Contain("user=S-123456;resolvedUser=123456@district.org;fromUtc=2026-10-01T12:00:00");
    }

    [SqlTheory]
    [InlineData(null, "S-123456", From, To, "case number")]
    [InlineData("  ", "S-123456", From, To, "case number")]
    [InlineData("IR-7", null, From, To, "@Device and/or @User")]
    [InlineData("IR-7", "S-123456", To, From, "earlier than")]
    public async Task InvalidRequests_AreRefused(string? caseNumber, string? user, string from, string to, string message)
    {
        var act = () => WebActivity(("@User", user), ("@From", from), ("@To", to), ("@TimesAreUtc", true), ("@CaseNumber", caseNumber));

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(message);
    }

    [SqlFact]
    public async Task OlderProcedures_AreAuditedToo_WithOptionalCaseNumber()
    {
        await Exec("dbo.usp_DeviceTimeline", ("@Device", "5CD1234XYZ"), ("@From", From), ("@To", To), ("@TimesAreUtc", true), ("@CaseNumber", "IR-8"));
        await Exec("dbo.usp_UserTimeline", ("@User", "S-123456"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));
        await Exec("dbo.usp_WhoWasOnIp", ("@Ip", "10.1.2.3"), ("@At", "2026-10-01T13:00:00"), ("@TimesAreUtc", true));
        await Exec("dbo.usp_FindDevice", ("@Search", "A-100"));

        (await db.ScalarAsync("""
            SELECT STRING_AGG(CONCAT(ProcedureName, ':', COALESCE(CaseNumber, '-')), ',') WITHIN GROUP (ORDER BY InvestigationAuditId)
            FROM dbo.InvestigationAudit
            """)).Should().Be("usp_DeviceTimeline:IR-8,usp_UserTimeline:-,usp_WhoWasOnIp:-,usp_FindDevice:-");
    }

    private async Task<(List<Dictionary<string, object?>> Rows, List<Dictionary<string, object>> Summary)> WebActivity(params (string Name, object? Value)[] parameters)
    {
        var sets = await Exec("dbo.usp_WebActivity", parameters);
        return (sets[0], sets[1].Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value!)).ToList());
    }

    private async Task<List<List<Dictionary<string, object?>>>> Exec(string procedure, params (string Name, object? Value)[] parameters)
    {
        await using var conn = new SqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(procedure, conn) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var sets = new List<List<Dictionary<string, object?>>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        do
        {
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
            sets.Add(rows);
        } while (await reader.NextResultAsync());
        return sets;
    }
}

public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlTestDatabase.EnvironmentVariable)))
            Skip = $"Set {SqlTestDatabase.EnvironmentVariable} to run SQL integration tests.";
    }
}
