using ChromebookCollector.Services;

namespace ChromebookCollector.Tests;

public sealed class EventNormalizerTests
{
    [Fact]
    public void Normalize_MapsCommonFields()
    {
        var records = EventNormalizer.Normalize([
            new ActivityEvent
            {
                Type = "NAVIGATION",
                ObservedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
                Payload = new Dictionary<string, object?> { ["url"] = "https://example.com", ["title"] = "Example" },
                Device = new DeviceContext { UserEmail = "user@example.com", DirectoryDeviceId = "device-1" }
            }
        ]).ToList();

        Assert.Single(records);
        Assert.Equal("NAVIGATION", records[0].EventType);
        Assert.Equal("https://example.com", records[0].Url);
        Assert.Equal("user@example.com", records[0].UserEmail);
    }
}
