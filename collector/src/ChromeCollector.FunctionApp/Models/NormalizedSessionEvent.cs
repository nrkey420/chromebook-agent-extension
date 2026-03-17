namespace ChromeCollector.FunctionApp.Models;

public sealed class NormalizedSessionEvent
{
    public Guid SessionId { get; set; }
    public string DirectoryDeviceId { get; set; } = "unknown";
    public string? DeviceSerial { get; set; }
    public string? UserEmail { get; set; }
    public DateTime SessionStartTimeUtc { get; set; }
    public DateTime? SessionEndTimeUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string AttributionConfidence { get; set; } = "LOW";
    public string? InternalIp { get; set; }
    public string? PublicIp { get; set; }
    public string? ExtensionVersion { get; set; }
    public string? OrgUnit { get; set; }
    public string? School { get; set; }
    public bool IsActive { get; set; } = true;
}
