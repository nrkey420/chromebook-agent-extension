using ChromeCollector.FunctionApp.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

public class HmacAuthTests
{
    [Fact]
    public void RejectsInvalidSignature()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"HMAC_KEYS__KEY1","dGVzdA=="}}).Build();
        var auth = new HmacAuth(cfg);
        var ok = auth.TryValidate("KEY1", "1", "bad", System.Text.Encoding.UTF8.GetBytes("{}"), out _);
        Assert.False(ok);
    }
}
