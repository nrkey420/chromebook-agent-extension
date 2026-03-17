using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using Xunit;

public class PayloadNormalizerTests
{
    [Fact]
    public void SetsEventCategory()
    {
        var n = new PayloadNormalizer();
        var d = n.Normalize(new ChromeEvent { EventType = "NAVIGATION" }, "k", null, null, "LOW");
        Assert.Equal("ACTIVITY", d["EventCategory_s"]);
    }
}
