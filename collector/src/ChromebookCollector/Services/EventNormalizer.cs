using System.Text.Json;

namespace ChromebookCollector.Services;

public static class EventNormalizer
{
    public static IEnumerable<SentinelRecord> Normalize(IEnumerable<ActivityEvent> events)
    {
        foreach (var evt in events)
        {
            evt.Payload.TryGetValue("url", out var url);
            evt.Payload.TryGetValue("title", out var title);
            evt.Payload.TryGetValue("state", out var state);
            evt.Payload.TryGetValue("danger", out var danger);

            yield return new SentinelRecord
            {
                TimeGenerated = evt.ObservedAt == default ? DateTimeOffset.UtcNow : evt.ObservedAt,
                EventType = evt.Type,
                Url = url?.ToString(),
                Title = title?.ToString(),
                DownloadState = state?.ToString(),
                DownloadDanger = danger?.ToString(),
                UserEmail = evt.Device.UserEmail,
                DirectoryDeviceId = evt.Device.DirectoryDeviceId,
                PayloadJson = JsonSerializer.Serialize(evt.Payload)
            };
        }
    }
}
