using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;

namespace ChromeCollector.FunctionApp.Tests;

public class PayloadNormalizerTests
{
    [Fact]
    public void Normalize_MapsExpectedFields()
    {
        var sut = new PayloadNormalizer();
        var evt = new ChromeEvent
        {
            EventType = "NAVIGATION",
            EventTimeUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            UserEmail = "user@example.com",
            DirectoryDeviceId = "device-123",
            DeviceSerial = "serial-456",
            Url = "https://example.com/page",
            Domain = "example.com",
            SessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        };

        var record = sut.Normalize(evt, "key-1", "203.0.113.5", "HIGH");

        record["TimeGenerated"].Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        record["EventType"].Should().Be("NAVIGATION");
        record["EventCategory"].Should().Be("ACTIVITY");
        record["UserEmail"].Should().Be("user@example.com");
        record["SessionId"].Should().Be(evt.SessionId);
        record["KeyId"].Should().Be("key-1");
        record["PublicIp"].Should().Be("203.0.113.5");
        record["AttributionConfidence"].Should().Be("HIGH");
    }

    [Fact]
    public void Normalize_SessionEventsAreCategorizedAsSession()
    {
        var record = new PayloadNormalizer().Normalize(new ChromeEvent { EventType = "HEARTBEAT" }, "k", null, "LOW");
        record["EventCategory"].Should().Be("SESSION");
    }
}
