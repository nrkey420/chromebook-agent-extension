namespace ChromebookCollector;

public sealed class EventBatchRequest
{
    public required List<ActivityEvent> Events { get; init; }
}

public sealed class ActivityEvent
{
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset ObservedAt { get; init; }
    public Dictionary<string, object?> Payload { get; init; } = new();
    public DeviceContext Device { get; init; } = new();
}

public sealed class DeviceContext
{
    public string? DirectoryDeviceId { get; init; }
    public string? SerialNumber { get; init; }
    public string? UserEmail { get; init; }
}

public sealed class SentinelRecord
{
    public DateTimeOffset TimeGenerated { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? DownloadState { get; init; }
    public string? DownloadDanger { get; init; }
    public string? UserEmail { get; init; }
    public string? DirectoryDeviceId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public string Source { get; init; } = "chromebook-extension";
}
