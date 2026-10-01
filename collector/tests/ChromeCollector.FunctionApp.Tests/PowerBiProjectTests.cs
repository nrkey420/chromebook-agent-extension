using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Data.SqlClient;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>
/// Keeps the Power BI project in reporting/powerbi consistent with itself and with the SQL schema:
/// the model parses, every field a report visual uses exists in the model, every column the model reads exists in
/// its SQL view, and the device-readers role can read all of it.
/// </summary>
public class PowerBiProjectTests
{
    private static string PowerBiRoot => Path.GetFullPath(Path.Combine(RepoPaths.CollectorRoot, "..", "reporting", "powerbi"));
    private static string ModelFolder => Path.Combine(PowerBiRoot, "ChromebookReporting.SemanticModel", "definition");
    private static string ReportFolder => Path.Combine(PowerBiRoot, "ChromebookReporting.Report", "definition");

    private static readonly Lazy<Database> Model = new(() => TmdlSerializer.DeserializeDatabaseFromFolder(ModelFolder));

    [Fact]
    public void SemanticModel_ParsesWithTom()
    {
        Model.Value.Model.Tables.Select(t => t.Name).Should().BeEquivalentTo(["Devices", "Users", "Logins", "Calendar", "SyncState", "IngestionErrors"]);
        Model.Value.Model.Relationships.Should().HaveCount(3);
    }

    [Fact]
    public void EveryReportField_ExistsInTheModel_AsTheRightKind()
    {
        var model = Model.Value.Model;
        var references = ReportJsonFiles().SelectMany(f => FieldReferences(JsonDocument.Parse(File.ReadAllText(f)).RootElement)
            .Select(r => (File: Path.GetRelativePath(ReportFolder, f), r.Kind, r.Entity, r.Property))).ToList();

        references.Should().NotBeEmpty();
        var problems = references.Where(r =>
        {
            var table = model.Tables.Find(r.Entity);
            if (table is null) return true;
            return r.Kind == "Measure" ? table.Measures.Find(r.Property) is null : table.Columns.Find(r.Property) is null;
        }).Select(r => $"{r.File}: {r.Kind} {r.Entity}[{r.Property}]").ToList();

        problems.Should().BeEmpty();
    }

    /// <summary>DAX is only evaluated by Power BI, so at least check that every Table[Column] and [Measure] it names exists.</summary>
    [Fact]
    public void MeasureFormulas_ReferenceExistingColumnsAndMeasures()
    {
        var model = Model.Value.Model;
        var measureNames = model.Tables.SelectMany(t => t.Measures).Select(m => m.Name).ToHashSet();
        var problems = new List<string>();
        foreach (var measure in model.Tables.SelectMany(t => t.Measures))
        {
            var dax = Regex.Replace(measure.Expression, "\"[^\"]*\"", "\"\""); // ignore string literals
            foreach (Match r in Regex.Matches(dax, @"(?<table>'[^']+'|\b[A-Za-z_][A-Za-z0-9_]*)?\[(?<name>[^\]]+)\]"))
            {
                var table = r.Groups["table"].Success ? r.Groups["table"].Value.Trim('\'') : null;
                var name = r.Groups["name"].Value;
                var ok = table is null
                    ? measureNames.Contains(name)
                    : model.Tables.Find(table)?.Columns.Find(name) is not null || model.Tables.Find(table)?.Measures.Find(name) is not null;
                if (!ok) problems.Add($"{measure.Name}: {r.Value}");
            }
            dax.Count(c => c == '(').Should().Be(dax.Count(c => c == ')'), $"parentheses in {measure.Name}");
        }
        problems.Should().BeEmpty();
    }

    [Fact]
    public void ReportPages_AreAllListedAndHaveAVisualOrADrillthrough()
    {
        var pagesJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReportFolder, "pages", "pages.json"))).RootElement;
        var order = pagesJson.GetProperty("pageOrder").EnumerateArray().Select(e => e.GetString()).ToList();
        Directory.GetDirectories(Path.Combine(ReportFolder, "pages")).Select(Path.GetFileName).Should().BeEquivalentTo(order);
        foreach (var page in order)
            Directory.Exists(Path.Combine(ReportFolder, "pages", page!, "visuals")).Should().BeTrue(page);
    }

    /// <summary>The model's SQL sources: view name and the columns each partition selects.</summary>
    public static IEnumerable<object[]> SqlSources()
    {
        foreach (var table in Model.Value.Model.Tables)
        {
            var m = (table.Partitions.Single().Source as MPartitionSource)?.Expression ?? "";
            var view = Regex.Match(m, @"Item\s*=\s*""(?<v>[^""]+)""");
            if (!view.Success) continue;
            var select = Regex.Match(m, @"Table\.SelectColumns\(View,\s*\{(?<c>[^}]*)\}\)");
            select.Success.Should().BeTrue($"{table.Name} should select its columns explicitly");
            var columns = Regex.Matches(select.Groups["c"].Value, @"""([^""]+)""").Select(x => x.Groups[1].Value).ToArray();
            yield return [table.Name, view.Groups["v"].Value, columns];
        }
    }

    [Fact]
    public void SqlSources_CoverEveryImportedTable_ExceptCalendar()
    {
        SqlSources().Select(s => (string)s[0]).Should().BeEquivalentTo(["Devices", "Users", "Logins", "SyncState", "IngestionErrors"]);
    }

    private static IEnumerable<string> ReportJsonFiles() =>
        Directory.GetFiles(ReportFolder, "*.json", SearchOption.AllDirectories);

    private static IEnumerable<(string Kind, string Entity, string Property)> FieldReferences(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var kind in new[] { "Column", "Measure" })
            {
                if (e.TryGetProperty(kind, out var c) && c.ValueKind == JsonValueKind.Object
                    && c.TryGetProperty("Property", out var prop)
                    && c.TryGetProperty("Expression", out var expr) && expr.TryGetProperty("SourceRef", out var sr)
                    && sr.TryGetProperty("Entity", out var entity))
                    yield return (kind, entity.GetString()!, prop.GetString()!);
            }
            foreach (var p in e.EnumerateObject())
                foreach (var r in FieldReferences(p.Value)) yield return r;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                foreach (var r in FieldReferences(item)) yield return r;
        }
    }
}

/// <summary>Checks the model's SQL sources against the real schema (needs SQL_TEST_CONNECTION_STRING).</summary>
public class PowerBiSqlSourceTests(SqlTestDatabase db) : IClassFixture<SqlTestDatabase>
{
    [SqlTheory]
    [MemberData(nameof(PowerBiProjectTests.SqlSources), MemberType = typeof(PowerBiProjectTests))]
    public async Task EveryModelColumn_ExistsInItsSqlView_AndDeviceReadersCanRefreshIt(string table, string view, string[] columns)
    {
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = new SqlCommand("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @v", conn))
        {
            cmd.Parameters.AddWithValue("@v", view);
            var actual = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) actual.Add(reader.GetString(0));
            columns.Except(actual).Should().BeEmpty($"Power BI table {table} reads these columns from dbo.{view}");
        }

        // The refresh identity only needs the device readers role.
        await using (var cmd = new SqlCommand("""
            IF USER_ID('pbi_refresh_test') IS NULL CREATE USER pbi_refresh_test WITHOUT LOGIN;
            ALTER ROLE ChromebookDeviceReaders ADD MEMBER pbi_refresh_test;
            """, conn))
            await cmd.ExecuteNonQueryAsync();

        var select = $"SELECT TOP (0) {string.Join(", ", columns.Select(c => $"[{c}]"))} FROM dbo.[{view}]";
        await using (var cmd = new SqlCommand($"EXECUTE AS USER = 'pbi_refresh_test';\n{select};\nREVERT;", conn))
            await cmd.ExecuteNonQueryAsync();
    }
}
