using ChromeCollector.FunctionApp.Services;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>
/// Parses the schema scripts and the collector's inline SQL with the SQL Server parser,
/// so syntax errors are caught in CI without needing a database.
/// </summary>
public class SqlSyntaxTests
{
    private static string SqlDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ChromeCollector.sln"))) dir = dir.Parent;
            dir.Should().NotBeNull("tests must run inside the collector folder");
            return Path.Combine(dir!.FullName, "src", "ChromeCollector.FunctionApp", "Sql");
        }
    }

    public static IEnumerable<object[]> SchemaScripts() =>
        new[] { "001_tables.sql", "002_views.sql", "003_procedures.sql" }.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(SchemaScripts))]
    public void SchemaScript_Parses(string fileName)
    {
        var path = Path.Combine(SqlDirectory, fileName);
        File.Exists(path).Should().BeTrue();
        AssertParses(File.ReadAllText(path));
    }

    [Fact]
    public void SchemaScripts_AreTheOnlySqlFiles()
    {
        Directory.GetFiles(SqlDirectory, "*.sql").Select(Path.GetFileName)
            .Should().BeEquivalentTo(SchemaScripts().Select(s => (string)s[0]));
    }

    [Fact]
    public void GrantFunctionIdentityScript_Parses()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(SqlDirectory, "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "infra", "scripts", "sql", "grant-function-identity.sql");
        File.Exists(path).Should().BeTrue(path);
        var sql = File.ReadAllText(path);
        sql.Should().Contain("$(FunctionAppName)");
        AssertParses(sql.Replace("$(FunctionAppName)", "scpschrome-func-poc"));
    }

    [Theory]
    [InlineData(nameof(SqlWriter.InsertEventSql))]
    [InlineData(nameof(SqlWriter.UpsertDeviceSql))]
    [InlineData(nameof(SqlWriter.InsertErrorSql))]
    public void CollectorSql_Parses(string constantName)
    {
        var sql = (string)typeof(SqlWriter).GetField(constantName)!.GetValue(null)!;
        AssertParses(sql);
    }

    [Theory]
    [InlineData(nameof(GoogleSyncStore.CreateDeviceStagingSql))]
    [InlineData(nameof(GoogleSyncStore.MergeDevicesSql))]
    [InlineData(nameof(GoogleSyncStore.CreateUserStagingSql))]
    [InlineData(nameof(GoogleSyncStore.MergeUsersSql))]
    [InlineData(nameof(GoogleSyncStore.CreateAuditStagingSql))]
    [InlineData(nameof(GoogleSyncStore.InsertAuditSql))]
    [InlineData(nameof(GoogleSyncStore.GetWatermarkSql))]
    [InlineData(nameof(GoogleSyncStore.RecordRunSql))]
    public void GoogleSyncSql_Parses(string constantName)
    {
        var sql = (string)typeof(GoogleSyncStore).GetField(constantName)!.GetValue(null)!;
        AssertParses(sql);
    }

    /// <summary>SqlBulkCopy maps by column name, so the staging tables and the DataTables must agree.</summary>
    [Fact]
    public void GoogleDeviceStaging_ColumnsMatchBulkCopyTables()
    {
        var device = new Models.GoogleDevice { DirectoryDeviceId = "d" };
        var now = DateTime.UtcNow;
        StagingColumns(GoogleSyncStore.CreateDeviceStagingSql, "#GoogleDevices")
            .Should().Equal(GoogleSyncStore.DeviceTable([device], now).Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        StagingColumns(GoogleSyncStore.CreateDeviceStagingSql, "#GoogleRecentUsers")
            .Should().Equal(GoogleSyncStore.RecentUserTable([device], now).Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        StagingColumns(GoogleSyncStore.CreateDeviceStagingSql, "#GoogleActiveTime")
            .Should().Equal(GoogleSyncStore.ActiveTimeTable([device], now).Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
    }

    private static List<string> StagingColumns(string createSql, string tableName)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(createSql);
        var script = (TSqlScript)parser.Parse(reader, out _);
        return script.Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>()
            .Single(s => s.SchemaObjectName.BaseIdentifier.Value == tableName)
            .Definition.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToList();
    }

    private static void AssertParses(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        errors.Select(e => $"line {e.Line}: {e.Message}").Should().BeEmpty();
    }
}
