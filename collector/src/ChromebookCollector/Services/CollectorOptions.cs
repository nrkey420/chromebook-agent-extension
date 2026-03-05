namespace ChromebookCollector.Services;

public sealed class CollectorOptions
{
    public Dictionary<string, string> HmacKeys { get; init; } = new();
    public int HmacAllowedSkewSeconds { get; init; } = 300;
    public string RawContainerName { get; init; } = "raw-events";
    public string SentinelEndpoint { get; init; } = string.Empty;
    public string SentinelDcrImmutableId { get; init; } = string.Empty;
    public string SentinelStreamName { get; init; } = "Custom-ChromebookActivity_CL";
}
