using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using Xunit;

public class SessionAttributionTests
{
    [Fact]
    public void HighConfidenceWhenUserAndDevicePresent()
    {
        var svc = new SessionAttributionService();
        var confidence = svc.CalculateConfidence(new ChromeEvent { UserEmail = "a@b", DirectoryDeviceId = "d", SessionId = Guid.NewGuid() });
        Assert.Equal("HIGH", confidence);
    }
}
