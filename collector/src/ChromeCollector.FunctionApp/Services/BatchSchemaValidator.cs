using ChromeCollector.FunctionApp.Models;

namespace ChromeCollector.FunctionApp.Services;

public static class BatchSchemaValidator
{
    public static bool TryValidate(ChromeBatch batch, out string? error)
    {
        if (batch.Events.Count == 0)
        {
            error = "Batch must include at least one event.";
            return false;
        }

        for (var i = 0; i < batch.Events.Count; i++)
        {
            var evt = batch.Events[i];
            if (string.IsNullOrWhiteSpace(evt.EventType))
            {
                error = $"Event at index {i} is missing EventType.";
                return false;
            }

            if (!evt.EventTimeUtc.HasValue)
            {
                error = $"Event at index {i} is missing EventTime.";
                return false;
            }
        }

        error = null;
        return true;
    }
}
