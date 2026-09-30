using System.Text.Json;
using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;

namespace ChromeCollector.FunctionApp.Tests;

/// <summary>
/// The Sentinel DCR stream and custom table are created from infra/sentinel/chromebook-activity-schema.json.
/// The Logs Ingestion API drops fields the stream does not declare, so the collector payload must match it exactly.
/// </summary>
public class SentinelSchemaTests
{
    private sealed record Column(string Name, string Type);
    private sealed record Schema(string TableName, string StreamName, List<Column> Columns);

    private static Schema LoadSchema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "infra", "sentinel"))) dir = dir.Parent;
        dir.Should().NotBeNull("tests must run inside the repository");
        var json = File.ReadAllText(Path.Combine(dir!.FullName, "infra", "sentinel", "chromebook-activity-schema.json"));
        return JsonSerializer.Deserialize<Schema>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static Dictionary<string, object?> SampleRecord() => new PayloadNormalizer().Normalize(
        new ChromeEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "NAVIGATION",
            EventTimeUtc = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid(),
            UserEmail = "student@district.org",
            DirectoryDeviceId = "device-1",
            DeviceSerial = "SN1",
            Url = "https://example.com/",
            Domain = "example.com"
        },
        "KEY1", "203.0.113.5", "HIGH");

    [Fact]
    public void PayloadFields_MatchDcrColumns()
    {
        var schema = LoadSchema();
        SampleRecord().Keys.Should().BeEquivalentTo(schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void PayloadValues_SerializeAsDeclaredColumnTypes()
    {
        var schema = LoadSchema();
        var serialized = JsonSerializer.SerializeToElement(new[] { SampleRecord() })[0];

        foreach (var column in schema.Columns)
        {
            var value = serialized.GetProperty(column.Name);
            if (value.ValueKind == JsonValueKind.Null) continue;

            switch (column.Type)
            {
                case "datetime":
                    value.ValueKind.Should().Be(JsonValueKind.String, column.Name);
                    DateTimeOffset.TryParse(value.GetString(), out _).Should().BeTrue(column.Name);
                    break;
                case "string":
                    value.ValueKind.Should().Be(JsonValueKind.String, column.Name);
                    break;
                default:
                    throw new InvalidOperationException($"Add a type check for column type '{column.Type}'.");
            }
        }
    }

    [Fact]
    public void Schema_DeclaresTimeGeneratedAndMatchingStreamName()
    {
        var schema = LoadSchema();
        schema.Columns.Should().ContainSingle(c => c.Name == "TimeGenerated" && c.Type == "datetime");
        schema.TableName.Should().EndWith("_CL");
        schema.StreamName.Should().Be("Custom-" + schema.TableName);
    }

    [Fact]
    public void LocalSettingsTemplate_UsesSchemaStreamName()
    {
        var schema = LoadSchema();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ChromeCollector.sln"))) dir = dir.Parent;
        var template = File.ReadAllText(Path.Combine(dir!.FullName, "src", "ChromeCollector.FunctionApp", "local.settings.json.template"));
        template.Should().Contain($"\"DCR_STREAM_NAME\": \"{schema.StreamName}\"");
    }
}
