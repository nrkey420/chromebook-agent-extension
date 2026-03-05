using System.Security.Cryptography;
using System.Text;
using ChromebookCollector.Services;
using Microsoft.Extensions.Options;

namespace ChromebookCollector.Tests;

public sealed class HmacValidatorTests
{
    [Fact]
    public void Validate_ReturnsTrue_ForValidSignature()
    {
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("super-secret"));
        var options = Options.Create(new CollectorOptions
        {
            HmacKeys = new Dictionary<string, string> { ["kid-1"] = secret },
            HmacAllowedSkewSeconds = 300
        });

        var validator = new HmacValidator(options);
        var body = "{\"events\":[{\"type\":\"HEARTBEAT\"}]}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}\n{body}")));

        Assert.True(validator.Validate("kid-1", timestamp, signature, body));
    }
}
