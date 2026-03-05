using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeCollector.FunctionApp.Models;

public sealed class ChromeEvent
{
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("eventTimeUtc")]
    public DateTimeOffset? EventTimeUtc { get; set; }

    [JsonPropertyName("userEmail")]
    public string? UserEmail { get; set; }

    [JsonPropertyName("directoryDeviceId")]
    public string? DirectoryDeviceId { get; set; }

    [JsonPropertyName("deviceSerial")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("downloadFileName")]
    public string? DownloadFileName { get; set; }

    [JsonPropertyName("downloadMime")]
    public string? DownloadMime { get; set; }

    [JsonPropertyName("downloadDanger")]
    public string? DownloadDanger { get; set; }

    [JsonPropertyName("downloadState")]
    public string? DownloadState { get; set; }

    [JsonPropertyName("extensionVersion")]
    public string? ExtensionVersion { get; set; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
