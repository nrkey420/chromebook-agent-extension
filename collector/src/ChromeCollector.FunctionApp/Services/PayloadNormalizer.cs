using ChromeCollector.FunctionApp.Models;

namespace ChromeCollector.FunctionApp.Services;

public interface IPayloadNormalizer
{
    Dictionary<string, object?> Normalize(ChromeEvent chromeEvent, string keyId, string? clientIp);
}

public sealed class PayloadNormalizer : IPayloadNormalizer
{
    public Dictionary<string, object?> Normalize(ChromeEvent chromeEvent, string keyId, string? clientIp)
    {
        return new Dictionary<string, object?>
        {
            ["EventType_s"] = chromeEvent.EventType,
            ["EventTimeUtc_t"] = chromeEvent.EventTimeUtc?.UtcDateTime,
            ["UserEmail_s"] = chromeEvent.UserEmail,
            ["DirectoryDeviceId_s"] = chromeEvent.DirectoryDeviceId,
            ["DeviceSerial_s"] = chromeEvent.DeviceSerial,
            ["Url_s"] = chromeEvent.Url,
            ["Domain_s"] = chromeEvent.Domain,
            ["Title_s"] = chromeEvent.Title,
            ["DownloadFileName_s"] = chromeEvent.DownloadFileName,
            ["DownloadMime_s"] = chromeEvent.DownloadMime,
            ["DownloadDanger_s"] = chromeEvent.DownloadDanger,
            ["DownloadState_s"] = chromeEvent.DownloadState,
            ["ExtensionVersion_s"] = chromeEvent.ExtensionVersion,
            ["SessionId_g"] = chromeEvent.SessionId,
            ["KeyId_s"] = keyId,
            ["CollectorTimeUtc_t"] = DateTime.UtcNow,
            ["ClientIp_s"] = clientIp
        };
    }
}
