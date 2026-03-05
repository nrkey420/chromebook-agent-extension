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
            Title = "Example",
            DownloadFileName = "file.txt",
            DownloadMime = "text/plain",
            DownloadDanger = "safe",
            DownloadState = "complete",
            ExtensionVersion = "1.0.0",
            SessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        };

        var record = sut.Normalize(evt, "key-1", "10.0.0.1");

        record["EventType_s"].Should().Be("NAVIGATION");
        record["UserEmail_s"].Should().Be("user@example.com");
        record["SessionId_g"].Should().Be(evt.SessionId);
        record["KeyId_s"].Should().Be("key-1");
        record["ClientIp_s"].Should().Be("10.0.0.1");
        record.Should().ContainKeys("CollectorTimeUtc_t", "EventTimeUtc_t");
    }
}
