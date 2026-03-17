using ChromeCollector.FunctionApp.Models;

namespace ChromeCollector.FunctionApp.Services;

public interface IPayloadNormalizer
{
    Dictionary<string, object?> Normalize(ChromeEvent chromeEvent, string keyId, string? clientIp, string? publicIp, string confidence);
}

public sealed class PayloadNormalizer : IPayloadNormalizer
{
    public Dictionary<string, object?> Normalize(ChromeEvent chromeEvent, string keyId, string? clientIp, string? publicIp, string confidence)
    {
        var eventCategory = chromeEvent.EventType is "NAVIGATION" or "DOWNLOAD" ? "ACTIVITY" : "SESSION";
        return new Dictionary<string, object?>
        {
            ["EventType_s"] = chromeEvent.EventType,
            ["EventTimeUtc_t"] = chromeEvent.EventTimeUtc?.UtcDateTime,
            ["UserEmail_s"] = chromeEvent.UserEmail,
            ["DirectoryDeviceId_s"] = chromeEvent.DirectoryDeviceId,
            ["DeviceSerial_s"] = chromeEvent.DeviceSerial,
            ["SessionId_g"] = chromeEvent.SessionId,
            ["Url_s"] = chromeEvent.Url,
            ["Domain_s"] = chromeEvent.Domain,
            ["Title_s"] = chromeEvent.Title,
            ["DownloadFileName_s"] = chromeEvent.DownloadFileName,
            ["DownloadMime_s"] = chromeEvent.DownloadMime,
            ["DownloadDanger_s"] = chromeEvent.DownloadDanger,
            ["DownloadState_s"] = chromeEvent.DownloadState,
            ["InternalIp_s"] = chromeEvent.InternalIp,
            ["PublicIp_s"] = publicIp,
            ["AttributionConfidence_s"] = confidence,
            ["ExtensionVersion_s"] = chromeEvent.ExtensionVersion,
            ["CollectorTimeUtc_t"] = DateTime.UtcNow,
            ["KeyId_s"] = keyId,
            ["ClientIp_s"] = clientIp,
            ["EventCategory_s"] = eventCategory
        };
    }
}
