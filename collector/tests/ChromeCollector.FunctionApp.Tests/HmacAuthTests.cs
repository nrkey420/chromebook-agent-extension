using System.Security.Cryptography;
using System.Text;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ChromeCollector.FunctionApp.Tests;

public class HmacAuthTests
{
    [Fact]
    public void TryValidate_ReturnsTrue_ForValidSignature()
    {
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("super-secret"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HMAC_KEYS:test"] = secret })
            .Build();

        var auth = new HmacAuth(config);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var signature = BuildSignature(secret, ts, body);

        var result = auth.TryValidate("test", ts, signature, body, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenTimestampSkewTooLarge()
    {
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("super-secret"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HMAC_KEYS:test"] = secret })
            .Build();

        var auth = new HmacAuth(config);
        var ts = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var signature = BuildSignature(secret, ts, body);

        var result = auth.TryValidate("test", ts, signature, body, out var error);

        result.Should().BeFalse();
        error.Should().Contain("skew");
    }

    private static string BuildSignature(string base64Secret, string timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(base64Secret));
        var payload = Encoding.UTF8.GetBytes($"{timestamp}\n{Encoding.UTF8.GetString(body)}");
        return Convert.ToBase64String(hmac.ComputeHash(payload));
    }
}
