using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>
/// Runs the lookup procedures (usp_UserDevices, usp_DeviceUsers, usp_IpLookup, usp_SiteVisitors) against SQL Server.
/// Scenario (UTC): student 123456 uses dev-1 (Google login + extension session) and dev-2 (extension session that
/// began the evening before); student 777777 signs in to dev-1 later, where the extension is not installed for them.
/// </summary>
public class LookupProcedureSqlTests(SqlTestDatabase db) : IClassFixture<SqlTestDatabase>, IAsyncLifetime
{
    private const string From = "2026-10-01T12:00:00";
    private const string To = "2026-10-02T00:00:00";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(db.ConnectionString)) return;
        await db.ExecAsync("""
            DELETE dbo.ActivityEvents; DELETE dbo.Sessions; DELETE dbo.IpObservations; DELETE dbo.GoogleAuditEvents;
            DELETE dbo.Devices; DELETE dbo.GoogleUsers; DELETE dbo.InvestigationAudit;
            UPDATE dbo.ReportingSettings SET SettingValue = 'Eastern Standard Time' WHERE SettingName = 'ReportingTimeZone';

            INSERT dbo.Devices (DirectoryDeviceId, SerialNumber, AssetId, AnnotatedLocation, OrgUnitPath)
            VALUES ('dev-1', '5CD1234XYZ', 'A-100', 'Room 204', '/Devices/HS'), ('dev-2', '5CD9999AAA', 'A-200', 'Library', '/Devices/HS');
            INSERT dbo.GoogleUsers (UserEmail, StudentId, OrgUnitPath, RefreshedUtc)
            VALUES ('123456@district.org', 'S-123456', '/Students/HS', SYSUTCDATETIME()),
                   ('777777@district.org', 'S-777777', '/Students/HS', SYSUTCDATETIME());

            INSERT dbo.GoogleAuditEvents (Application, UniqueQualifier, EventTimeUtc, EventName, UserEmail, ActorEmail, IpAddress, DirectoryDeviceId)
            VALUES
              -- Google sometimes reports the user only as the actor.
              ('chrome', 'q1', '2026-10-01T13:00:00', 'CHROME_OS_LOGIN_EVENT', NULL, '123456@district.org', NULL, 'dev-1'),
              ('chrome', 'q2', '2026-10-01T14:59:00', 'CHROME_OS_LOGIN_FAILURE_EVENT', '777777@district.org', NULL, NULL, 'dev-1'),
              ('chrome', 'q3', '2026-10-01T15:00:00', 'CHROME_OS_LOGIN_EVENT', '777777@district.org', NULL, NULL, 'dev-1'),
              ('chrome', 'q4', '2026-10-01T16:30:00', 'CHROME_OS_LOGOUT_EVENT', '777777@district.org', NULL, NULL, 'dev-1'),
              ('chrome', 'q5', '2026-09-01T13:00:00', 'CHROME_OS_LOGIN_EVENT', '123456@district.org', NULL, NULL, 'dev-2'),
              ('login',  'q6', '2026-10-01T12:59:00', 'login_success', '123456@district.org', NULL, '203.0.113.9', NULL),
              -- For the default-window tests: yesterday and 40 days ago.
              ('chrome', 'q7', DATEADD(day, -1, SYSUTCDATETIME()), 'CHROME_OS_LOGIN_EVENT', '777777@district.org', NULL, NULL, 'dev-2'),
              ('chrome', 'q8', DATEADD(day, -40, SYSUTCDATETIME()), 'CHROME_OS_LOGIN_EVENT', '777777@district.org', NULL, NULL, 'dev-2');

            INSERT dbo.Sessions (SessionId, DirectoryDeviceId, UserEmail, SessionStartUtc, SessionEndUtc, LastSeenUtc,
                                 FirstInternalIp, LastInternalIp, FirstPublicIp, LastPublicIp, IsActive)
            VALUES
              (NEWID(), 'dev-1', '123456@district.org', '2026-10-01T13:00:05', '2026-10-01T14:00:00', '2026-10-01T14:00:00',
               '10.20.30.40', '10.20.30.40', '203.0.113.9', '203.0.113.9', 0),
              (NEWID(), 'dev-2', '123456@district.org', '2026-09-30T23:00:00', NULL, '2026-10-01T12:30:00',
               '10.20.31.7', '10.20.31.7', '203.0.113.9', '203.0.113.9', 0),
              (NEWID(), 'dev-2', NULL, '2026-10-01T16:00:00', '2026-10-01T16:20:00', '2026-10-01T16:20:00', NULL, NULL, NULL, NULL, 0);

            INSERT dbo.IpObservations (ObservedUtc, Source, DirectoryDeviceId, UserEmail, InternalIp, PublicIp)
            VALUES
              ('2026-10-01T13:00:05', 'EXTENSION', 'dev-1', '123456@district.org', '10.20.30.40', '203.0.113.9'),
              ('2026-10-01T13:30:00', 'EXTENSION', 'dev-1', '123456@district.org', '10.20.30.40', '203.0.113.9'),
              ('2026-10-01T15:05:00', 'GOOGLE_SYNC', 'dev-1', NULL, '10.20.30.41', '203.0.113.9'),
              ('2026-10-01T12:15:00', 'EXTENSION', 'dev-2', '123456@district.org', '10.20.31.7', '203.0.113.9'),
              ('2026-09-15T13:00:00', 'EXTENSION', 'dev-2', '123456@district.org', '10.20.30.40', '203.0.113.9');

            INSERT dbo.ActivityEvents (EventId, DirectoryDeviceId, UserEmail, EventType, EventTimeUtc, Url, Domain, Title, DownloadFileName)
            VALUES
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:05:00', 'https://example.com/', 'example.com', 'Example', NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:06:00', 'https://mail.example.com/', 'mail.example.com', 'Mail', NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-01T13:07:00', 'https://notexample.com/', 'notexample.com', 'Not it', NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'DOWNLOAD',   '2026-10-01T13:10:00', 'https://example.com/a.exe', 'example.com', NULL, 'a.exe'),
              (NEWID(), 'dev-1', '123456@district.org', 'HEARTBEAT',  '2026-10-01T13:15:00', NULL, NULL, NULL, NULL),
              (NEWID(), 'dev-2', '777777@district.org', 'NAVIGATION', '2026-10-01T15:10:00', 'https://www.example.com/watch?v=abc_1', 'example.com', 'Video', NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', '2026-10-03T13:00:00', 'https://example.com/later', 'example.com', 'Outside window', NULL),
              (NEWID(), 'dev-1', '123456@district.org', 'NAVIGATION', DATEADD(hour, -1, SYSUTCDATETIME()), 'https://recent.example.org/', 'recent.example.org', 'Recent', NULL),
              (NEWID(), 'dev-2', '777777@district.org', 'NAVIGATION', DATEADD(day, -40, SYSUTCDATETIME()), 'https://old.example.org/', 'old.example.org', 'Old', NULL);
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- usp_UserDevices ----------

    [SqlFact]
    public async Task UserDevices_ByStudentId_ListsEachDeviceWithEvidence_MostRecentFirst()
    {
        var (devices, signIns) = await Lookup("dbo.usp_UserDevices", ("@User", "S-123456"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        devices.Select(d => d["SerialNumber"]).Should().Equal("5CD1234XYZ", "5CD9999AAA");
        var dev1 = devices[0];
        dev1["AssetId"].Should().Be("A-100");
        dev1["ChromeOsLogins"].Should().Be(1, "the Google login reported with only an actor email still counts");
        dev1["ExtensionSessions"].Should().Be(1);
        dev1["Evidence"].Should().Be("GOOGLE_AND_EXTENSION");
        dev1["LastInternalIp"].Should().Be("10.20.30.40");
        dev1["FirstSignInUtc"].Should().Be(new DateTime(2026, 10, 1, 13, 0, 0));
        dev1["FirstSignInLocal"].Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));

        var dev2 = devices[1];
        dev2["Evidence"].Should().Be("EXTENSION_ONLY");
        dev2["FirstSignInUtc"].Should().Be(new DateTime(2026, 9, 30, 23, 0, 0), "a session already open when the window starts is included");
        dev2["ChromeOsLogins"].Should().Be(0, "the September login is outside the window");

        signIns.Should().HaveCount(3);
        signIns.Select(s => s["UserEmail"]).Should().AllBeEquivalentTo("123456@district.org");
        signIns.Select(s => s["StudentId"]).Should().AllBeEquivalentTo("S-123456");
    }

    [SqlFact]
    public async Task UserDevices_LocalTimes_AndAudit()
    {
        // 09:00-10:00 Eastern daylight = 13:00-14:00 UTC: only dev-1 (dev-2's session ended 12:30 UTC).
        var (devices, _) = await Lookup("dbo.usp_UserDevices", ("@User", "123456@District.org"),
            ("@From", "2026-10-01T09:00:00"), ("@To", "2026-10-01T10:00:00"), ("@CaseNumber", "IR-20"));

        devices.Select(d => d["DirectoryDeviceId"]).Should().Equal("dev-1");
        (await db.ScalarAsync("SELECT CONCAT(ProcedureName, '|', RowsReturned) FROM dbo.InvestigationAudit WHERE CaseNumber = 'IR-20'"))
            .Should().Be("usp_UserDevices|1");
        ((string)(await db.ScalarAsync("SELECT Parameters FROM dbo.InvestigationAudit WHERE CaseNumber = 'IR-20'"))!)
            .Should().Contain("resolvedUser=123456@district.org;fromUtc=2026-10-01T13:00:00");
    }

    // ---------- usp_DeviceUsers ----------

    [SqlFact]
    public async Task DeviceUsers_ByAssetTag_ShowsTheUserTheExtensionMissed()
    {
        var (users, signIns) = await Lookup("dbo.usp_DeviceUsers", ("@Device", "A-100"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        users.Select(u => u["UserEmail"]).Should().Equal("777777@district.org", "123456@district.org");
        users[0]["Evidence"].Should().Be("GOOGLE_ONLY");
        users[0]["StudentId"].Should().Be("S-777777");
        users[0]["LoginFailures"].Should().Be(1);
        users[0]["UserOrgUnit"].Should().Be("/Students/HS");
        users[1]["Evidence"].Should().Be("GOOGLE_AND_EXTENSION");

        signIns.Select(s => s["SignInType"]).Should().Equal("LOGIN", "SESSION", "LOGIN_FAILURE", "LOGIN");
        signIns.Select(s => s["SerialNumber"]).Should().AllBeEquivalentTo("5CD1234XYZ");
    }

    [SqlFact]
    public async Task DeviceUsers_BySerial_IncludesSessionsWithoutAKnownUser()
    {
        var (users, _) = await Lookup("dbo.usp_DeviceUsers", ("@Device", "5CD9999AAA"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        users.Select(u => u["UserEmail"]).Should().BeEquivalentTo(new object?[] { null, "123456@district.org" });
    }

    [SqlFact]
    public async Task DeviceUsers_WithoutDates_CoversTheLast30Days()
    {
        var (users, _) = await Lookup("dbo.usp_DeviceUsers", ("@Device", "A-200"));

        users.Single(u => (string?)u["UserEmail"] == "777777@district.org")["ChromeOsLogins"].Should().Be(1, "yesterday's login counts, not the one 40 days ago");
    }

    // ---------- usp_IpLookup ----------

    [SqlFact]
    public async Task IpLookup_ExactInternalIp_IsLimitedToTheWindow()
    {
        var (rows, _) = await Lookup("dbo.usp_IpLookup", ("@Ip", "10.20.30.40"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        var row = rows.Should().ContainSingle().Subject;
        row["MatchedOn"].Should().Be("INTERNAL");
        row["SerialNumber"].Should().Be("5CD1234XYZ");
        row["UserEmail"].Should().Be("123456@district.org");
        row["Observations"].Should().Be(2);
        row["FirstSeenUtc"].Should().Be(new DateTime(2026, 10, 1, 13, 0, 5));
        row["LastSeenUtc"].Should().Be(new DateTime(2026, 10, 1, 13, 30, 0));

        var (wider, _) = await Lookup("dbo.usp_IpLookup", ("@Ip", "10.20.30.40"), ("@From", "2026-09-01"), ("@To", To), ("@TimesAreUtc", true));
        wider.Select(r => r["DirectoryDeviceId"]).Should().Equal("dev-2", "dev-1");
    }

    [SqlFact]
    public async Task IpLookup_Prefix_FindsEveryAddressInTheRange_WithInferredUsers()
    {
        var (rows, _) = await Lookup("dbo.usp_IpLookup", ("@Ip", "10.20.30.*"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        rows.Select(r => r["MatchedIp"]).Should().Equal("10.20.30.40", "10.20.30.41");
        var inferred = rows[1];
        inferred["UserEmail"].Should().Be("777777@district.org");
        inferred["UserSource"].Should().Be("INFERRED_FROM_GOOGLE_LOGIN");
    }

    [SqlFact]
    public async Task IpLookup_PublicIp_ReturnsEveryDeviceBehindIt_AndGoogleSignInsFromIt()
    {
        var (rows, googleSignIns) = await Lookup("dbo.usp_IpLookup", ("@Ip", "203.0.113.9"), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        rows.Select(r => r["MatchedOn"]).Should().AllBeEquivalentTo("PUBLIC");
        rows.Select(r => r["DirectoryDeviceId"]).Distinct().Should().BeEquivalentTo(["dev-1", "dev-2"]);
        googleSignIns.Should().ContainSingle().Which["LoginEvent"].Should().Be("LOGIN_SUCCESS");
    }

    [SqlTheory]
    [InlineData(null, "Specify @Ip")]
    [InlineData("10.*", "at least 4 characters")]
    [InlineData("10.20.*.1", "single trailing *")]
    [InlineData("10.0.0.1' OR 1=1 --", "single trailing *")]
    [InlineData("10.%", "single trailing *")]
    public async Task IpLookup_InvalidAddresses_AreRefused(string? ip, string message)
    {
        var act = () => Lookup("dbo.usp_IpLookup", ("@Ip", ip), ("@From", From), ("@To", To), ("@TimesAreUtc", true));

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(message);
    }

    // ---------- usp_SiteVisitors ----------

    [SqlFact]
    public async Task SiteVisitors_PastedUrl_MatchesDomainAndSubdomains_PerUserSummary()
    {
        var (users, visits) = await Lookup("dbo.usp_SiteVisitors", ("@Domain", "https://www.Example.com/some/page"),
            ("@From", From), ("@To", To), ("@TimesAreUtc", true), ("@CaseNumber", "IR-30"));

        users.Select(u => u["UserEmail"]).Should().Equal("123456@district.org", "777777@district.org");
        users[0]["Visits"].Should().Be(2);
        users[0]["Downloads"].Should().Be(1);
        users[0]["Domains"].Should().Be(2);
        users[0]["StudentId"].Should().Be("S-123456");
        users[1]["Visits"].Should().Be(1);

        visits.Select(v => v["Domain"]).Should().Equal("example.com", "mail.example.com", "example.com", "example.com");
        visits.Select(v => v["Truncated"]).Should().AllBeEquivalentTo(false);
        visits[0]["SerialNumber"].Should().Be("5CD1234XYZ");
        visits[0]["CaseNumber"].Should().Be("IR-30");
    }

    [SqlFact]
    public async Task SiteVisitors_ExactDomain_UrlContains_AndDownloadsOff()
    {
        var (exact, _) = await Lookup("dbo.usp_SiteVisitors", ("@Domain", "example.com"), ("@IncludeSubdomains", false), ("@IncludeDownloads", false),
            ("@From", From), ("@To", To), ("@TimesAreUtc", true), ("@CaseNumber", "IR-31"));
        exact.Single(u => (string)u["UserEmail"]! == "123456@district.org")["Visits"].Should().Be(1);
        exact.Sum(u => (int)u["Downloads"]!).Should().Be(0);

        // Underscore is a LIKE wildcard; it must match literally.
        var (video, _) = await Lookup("dbo.usp_SiteVisitors", ("@Domain", "example.com"), ("@UrlContains", "v=abc_1"),
            ("@From", From), ("@To", To), ("@TimesAreUtc", true), ("@CaseNumber", "IR-31"));
        video.Select(u => u["UserEmail"]).Should().Equal("777777@district.org");
    }

    [SqlFact]
    public async Task SiteVisitors_MaxRows_TruncatesAndAudits_DefaultWindowIsLast30Days()
    {
        var (users, visits) = await Lookup("dbo.usp_SiteVisitors", ("@Domain", "example.com"), ("@From", From), ("@To", To),
            ("@TimesAreUtc", true), ("@MaxRows", 1), ("@CaseNumber", "IR-32"));
        visits.Should().ContainSingle().Which["Truncated"].Should().Be(true);
        users.Sum(u => (int)u["Visits"]! + (int)u["Downloads"]!).Should().Be(4, "the summary covers everything that matched");
        (await db.ScalarAsync("SELECT CONCAT(ProcedureName, '|', RowsReturned) FROM dbo.InvestigationAudit WHERE CaseNumber = 'IR-32'"))
            .Should().Be("usp_SiteVisitors|1");

        var (recent, _) = await Lookup("dbo.usp_SiteVisitors", ("@Domain", "example.org"), ("@CaseNumber", "IR-33"));
        recent.Select(u => u["UserEmail"]).Should().Equal("123456@district.org");
    }

    [SqlTheory]
    [InlineData(null, "example.com", "case number")]
    [InlineData("IR-34", null, "Specify @Domain")]
    [InlineData("IR-34", "exa%mple.com", "host name")]
    [InlineData("IR-34", "example.com' OR 1=1 --", "host name")]
    public async Task SiteVisitors_InvalidRequests_AreRefused(string? caseNumber, string? domain, string message)
    {
        var act = () => Lookup("dbo.usp_SiteVisitors", ("@Domain", domain), ("@CaseNumber", caseNumber));

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain(message);
    }

    // ---------- usp_WebActivity ----------

    [SqlFact]
    public async Task WebActivity_WithoutDates_CoversTheLast30Days()
    {
        var sets = await SqlProcedure.ExecAsync(db.ConnectionString, "dbo.usp_WebActivity", ("@User", "S-123456"), ("@CaseNumber", "IR-35"));

        sets[0].Select(r => r["Domain"]).Should().Contain("recent.example.org");
        (await SqlProcedure.ExecAsync(db.ConnectionString, "dbo.usp_WebActivity", ("@User", "S-777777"), ("@CaseNumber", "IR-35")))[0]
            .Select(r => r["Domain"]).Should().NotContain("old.example.org", "it is 40 days old");
    }

    private async Task<(List<Dictionary<string, object?>> First, List<Dictionary<string, object?>> Second)> Lookup(string procedure, params (string Name, object? Value)[] parameters)
    {
        var sets = await SqlProcedure.ExecAsync(db.ConnectionString, procedure, parameters);
        return (sets[0], sets[1]);
    }
}
