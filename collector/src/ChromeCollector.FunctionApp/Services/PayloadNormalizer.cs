using ChromeCollector.FunctionApp.Models;

namespace ChromeCollector.FunctionApp.Services;

public interface IPayloadNormalizer
{
    Dictionary<string, object?> Normalize(ChromeEvent chromeEvent, string keyId, string? publicIp, string confidence);
}

/// <summary>Shapes events for the Sentinel custom table (Logs Ingestion API).</summary>
public sealed class PayloadNormalizer : IPayloadNormalizer
{
    public Dictionary<string, object?> Normalize(ChromeEvent chromeEvent, string keyId, string? publicIp, string confidence)
    {
        var eventCategory = chromeEvent.EventType is "NAVIGATION" or "DOWNLOAD" ? "ACTIVITY" : "SESSION";
        return new Dictionary<string, object?>
        {
            ["TimeGenerated"] = chromeEvent.EventTimeUtc?.UtcDateTime ?? DateTime.UtcNow,
            ["EventId"] = chromeEvent.EventId,
            ["EventType"] = chromeEvent.EventType,
            ["EventCategory"] = eventCategory,
            ["UserEmail"] = chromeEvent.UserEmail,
            ["DirectoryDeviceId"] = chromeEvent.DirectoryDeviceId,
            ["DeviceSerial"] = chromeEvent.DeviceSerial,
            ["SessionId"] = chromeEvent.SessionId,
            ["Url"] = chromeEvent.Url,
            ["Domain"] = chromeEvent.Domain,
            ["Title"] = chromeEvent.Title,
            ["SearchQuery"] = chromeEvent.SearchQuery,
            ["DownloadFileName"] = chromeEvent.DownloadFileName,
            ["DownloadDanger"] = chromeEvent.DownloadDanger,
            ["DownloadState"] = chromeEvent.DownloadState,
            ["InternalIp"] = chromeEvent.InternalIp,
            ["PublicIp"] = publicIp,
            ["MacAddress"] = chromeEvent.MacAddress,
            ["AttributionConfidence"] = confidence,
            ["ExtensionVersion"] = chromeEvent.ExtensionVersion,
            ["KeyId"] = keyId
        };
    }
}
