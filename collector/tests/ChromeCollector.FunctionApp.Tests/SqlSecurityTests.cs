using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>
/// Checks the roles from 004_security.sql by impersonating a member of each role.
/// "allowed" statements must run; the others must fail with permission denied (error 229).
/// </summary>
public class SqlSecurityTests(SqlTestDatabase db) : IClassFixture<SqlTestDatabase>, IAsyncLifetime
{
    private const string WebActivity =
        "EXEC dbo.usp_WebActivity @User = 'x@district.org', @From = '2026-10-01', @To = '2026-10-02', @TimesAreUtc = 1, @CaseNumber = 'SEC-1'";
    private const string DeviceTimeline =
        "EXEC dbo.usp_DeviceTimeline @Device = 'SN-1', @From = '2026-10-01', @To = '2026-10-02', @TimesAreUtc = 1";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(db.ConnectionString)) return;
        await db.ExecAsync("""
            IF USER_ID('test_helpdesk') IS NULL CREATE USER test_helpdesk WITHOUT LOGIN;
            IF USER_ID('test_analyst') IS NULL CREATE USER test_analyst WITHOUT LOGIN;
            IF USER_ID('test_reviewer') IS NULL CREATE USER test_reviewer WITHOUT LOGIN;
            IF USER_ID('test_analyst_reader') IS NULL CREATE USER test_analyst_reader WITHOUT LOGIN;
            ALTER ROLE ChromebookDeviceReaders ADD MEMBER test_helpdesk;
            ALTER ROLE ChromebookInvestigators ADD MEMBER test_analyst;
            ALTER ROLE ChromebookAuditReviewers ADD MEMBER test_reviewer;
            -- An analyst who was also (wrongly) given broad read access.
            ALTER ROLE ChromebookInvestigators ADD MEMBER test_analyst_reader;
            ALTER ROLE db_datareader ADD MEMBER test_analyst_reader;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SqlTheory]
    // Helpdesk / ops: devices, users, logins, health; no web content, no investigations.
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.vw_Devices", true)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.vw_Users", true)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.vw_LoginHistory", true)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.SyncState", true)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.IngestionErrors", true)]
    [InlineData("test_helpdesk", "EXEC dbo.usp_FindDevice @Search = 'SN-1'", true)]
    [InlineData("test_helpdesk", "SELECT dbo.fn_ToLocal(SYSUTCDATETIME())", true)]
    [InlineData("test_helpdesk", WebActivity, false)]
    [InlineData("test_helpdesk", DeviceTimeline, false)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.vw_IpHistory", false)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.vw_WebActivity", false)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.ActivityEvents", false)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.Sessions", false)]
    [InlineData("test_helpdesk", "SELECT TOP 1 * FROM dbo.InvestigationAudit", false)]
    // IR analysts: everything through the audited procedures; no direct web content; cannot touch the audit.
    [InlineData("test_analyst", WebActivity, true)]
    [InlineData("test_analyst", DeviceTimeline, true)]
    [InlineData("test_analyst", "EXEC dbo.usp_UserTimeline @User = 'x', @From = '2026-10-01', @To = '2026-10-02'", true)]
    [InlineData("test_analyst", "EXEC dbo.usp_WhoWasOnIp @Ip = '10.0.0.1', @At = '2026-10-01T09:00:00'", true)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.vw_IpHistory", true)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.vw_LoginHistory", true)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.ActivityEvents", false)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.vw_WebActivity", false)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.vw_SearchActivity", false)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.vw_Downloads", false)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.vw_InvestigationTimeline", false)]
    [InlineData("test_analyst", "SELECT TOP 1 * FROM dbo.InvestigationAudit", false)]
    [InlineData("test_analyst", "DELETE dbo.InvestigationAudit", false)]
    [InlineData("test_analyst", "EXEC dbo.usp_LogInvestigation N'usp_WebActivity', N'FAKE', N'forged'", false)]
    // Broad read access does not reopen web content or the audit (DENY wins).
    [InlineData("test_analyst_reader", "SELECT TOP 1 * FROM dbo.ActivityEvents", false)]
    [InlineData("test_analyst_reader", "SELECT TOP 1 * FROM dbo.vw_WebActivity", false)]
    [InlineData("test_analyst_reader", "SELECT TOP 1 * FROM dbo.vw_InvestigationTimeline", false)]
    [InlineData("test_analyst_reader", "DELETE dbo.InvestigationAudit", false)]
    [InlineData("test_analyst_reader", WebActivity, true)]
    // Audit reviewers: read the audit trail, nothing else, and cannot change it.
    [InlineData("test_reviewer", "SELECT TOP 1 RunUtc, dbo.fn_ToLocal(RunUtc) FROM dbo.InvestigationAudit", true)]
    [InlineData("test_reviewer", "UPDATE dbo.InvestigationAudit SET CaseNumber = 'X'", false)]
    [InlineData("test_reviewer", "DELETE dbo.InvestigationAudit", false)]
    [InlineData("test_reviewer", WebActivity, false)]
    [InlineData("test_reviewer", "SELECT TOP 1 * FROM dbo.vw_Devices", false)]
    public async Task RolePermissions(string user, string sql, bool allowed)
    {
        var act = () => RunAsAsync(user, sql);

        if (allowed)
            await act.Should().NotThrowAsync();
        else
            (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(229, "expected permission denied for: {0}", sql);
    }

    [SqlFact]
    public async Task ProceduresStillWriteTheAuditForRoleMembers_AndTheSecurityScriptIsRerunnable()
    {
        await RunAsAsync("test_analyst", WebActivity.Replace("SEC-1", "SEC-AUDIT"));
        (await db.ScalarAsync("SELECT COUNT(*) FROM dbo.InvestigationAudit WHERE CaseNumber = 'SEC-AUDIT' AND ProcedureName = 'usp_WebActivity'"))
            .Should().Be(1);

        var script = await File.ReadAllTextAsync(Path.Combine(RepoPaths.CollectorRoot, "src", "ChromeCollector.FunctionApp", "Sql", "004_security.sql"));
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
            if (!string.IsNullOrWhiteSpace(batch)) await db.ExecAsync(batch);

        await RunAsAsync("test_analyst", WebActivity);
    }

    private async Task RunAsAsync(string user, string sql)
    {
        // No pooling: an impersonation left behind by a failed statement must not leak into another test.
        var cs = new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"EXECUTE AS USER = '{user}';\n{sql};\nREVERT;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.NextResultAsync() || await reader.ReadAsync()) { }
    }
}
