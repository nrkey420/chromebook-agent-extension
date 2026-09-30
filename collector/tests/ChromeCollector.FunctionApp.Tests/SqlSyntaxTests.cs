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

    [Theory]
    [InlineData(nameof(SqlWriter.InsertEventSql))]
    [InlineData(nameof(SqlWriter.UpsertDeviceSql))]
    [InlineData(nameof(SqlWriter.InsertErrorSql))]
    public void CollectorSql_Parses(string constantName)
    {
        var sql = (string)typeof(SqlWriter).GetField(constantName)!.GetValue(null)!;
        AssertParses(sql);
    }

    private static void AssertParses(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        errors.Select(e => $"line {e.Line}: {e.Message}").Should().BeEmpty();
    }
}
