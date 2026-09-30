using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeCollector.FunctionApp.Models;

public sealed class ChromeEvent
{
    [JsonPropertyName("eventId")]
    public Guid? EventId { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("eventTimeUtc")]
    public DateTimeOffset? EventTimeUtc { get; set; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    [JsonPropertyName("userEmail")]
    public string? UserEmail { get; set; }

    [JsonPropertyName("directoryDeviceId")]
    public string? DirectoryDeviceId { get; set; }

    [JsonPropertyName("serialNumber")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("assetId")]
    public string? AssetId { get; set; }

    [JsonPropertyName("annotatedLocation")]
    public string? AnnotatedLocation { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("chromeVersion")]
    public string? ChromeVersion { get; set; }

    [JsonPropertyName("platformVersion")]
    public string? PlatformVersion { get; set; }

    [JsonPropertyName("internalIp")]
    public string? InternalIp { get; set; }

    [JsonPropertyName("internalIpv6")]
    public string? InternalIpv6 { get; set; }

    [JsonPropertyName("macAddress")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("publicIp")]
    public string? PublicIp { get; set; }

    [JsonPropertyName("orgUnit")]
    public string? OrgUnit { get; set; }

    [JsonPropertyName("school")]
    public string? School { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("transition")]
    public string? Transition { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

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

    // Derived by the collector (EventEnricher), not sent by the extension.
    [JsonPropertyName("searchEngine")]
    public string? SearchEngine { get; set; }

    [JsonPropertyName("searchQuery")]
    public string? SearchQuery { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
