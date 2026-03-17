using ChromeCollector.FunctionApp.Models;

namespace ChromeCollector.FunctionApp.Services;

public interface ISessionAttributionService
{
    string CalculateConfidence(ChromeEvent chromeEvent);
}

public sealed class SessionAttributionService : ISessionAttributionService
{
    public string CalculateConfidence(ChromeEvent chromeEvent)
    {
        if (!string.IsNullOrWhiteSpace(chromeEvent.UserEmail) && !string.IsNullOrWhiteSpace(chromeEvent.DirectoryDeviceId) && chromeEvent.SessionId.HasValue)
            return "HIGH";
        if (!string.IsNullOrWhiteSpace(chromeEvent.UserEmail) && !string.IsNullOrWhiteSpace(chromeEvent.DeviceSerial))
            return "MEDIUM";
        return "LOW";
    }
}
