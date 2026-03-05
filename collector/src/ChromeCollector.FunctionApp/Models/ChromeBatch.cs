using System.Text.Json.Serialization;

namespace ChromeCollector.FunctionApp.Models;

public sealed class ChromeBatch
{
    [JsonPropertyName("events")]
    public List<ChromeEvent> Events { get; set; } = [];
}
