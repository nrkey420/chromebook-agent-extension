namespace ChromeCollector.FunctionApp.Models;

public sealed class NormalizedActivityEvent
{
    public Guid SessionId { get; set; }
    public string DirectoryDeviceId { get; set; } = "unknown";
    public string? UserEmail { get; set; }
    public string EventType { get; set; } = "UNKNOWN";
    public DateTime EventTimeUtc { get; set; }
    public string? Url { get; set; }
    public string? Domain { get; set; }
    public string? Title { get; set; }
    public string? DownloadFileName { get; set; }
    public string? DownloadMime { get; set; }
    public string? DownloadDanger { get; set; }
    public string? DownloadState { get; set; }
    public string? InternalIp { get; set; }
    public string? PublicIp { get; set; }
    public string AttributionConfidence { get; set; } = "LOW";
}
