using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ChromeCollector.FunctionApp.Services;

public interface IHmacAuth
{
    bool TryValidate(string keyId, string timestampHeader, string signatureHeader, byte[] bodyBytes, out string? error);
}

public sealed class HmacAuth(IConfiguration configuration) : IHmacAuth
{
    private const int AllowedSkewSeconds = 300;

    public bool TryValidate(string keyId, string timestampHeader, string signatureHeader, byte[] bodyBytes, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            error = "Missing required HMAC headers.";
            return false;
        }

        if (!TryParseTimestamp(timestampHeader, out var timestampUtc))
        {
            error = "Invalid X-Timestamp header.";
            return false;
        }

        var skew = Math.Abs((DateTimeOffset.UtcNow - timestampUtc).TotalSeconds);
        if (skew > AllowedSkewSeconds)
        {
            error = "X-Timestamp exceeds allowed skew.";
            return false;
        }

        var secret = ResolveSecret(keyId);
        if (secret is null)
        {
            error = "Unknown key id.";
            return false;
        }

        var bodyUtf8 = Encoding.UTF8.GetString(bodyBytes);
        var payload = Encoding.UTF8.GetBytes($"{timestampHeader}\n{bodyUtf8}");
        var keyBytes = DecodeSecret(secret);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payload);
        var expected = Convert.ToBase64String(hash);

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signatureHeader.Trim())))
        {
            error = "Invalid signature.";
            return false;
        }

        return true;
    }

    private string? ResolveSecret(string keyId)
    {
        var direct = configuration[$"HMAC_KEYS:{keyId}"] ?? configuration[$"HMAC_KEYS__{keyId}"];
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var jsonMap = configuration["HMAC_KEYS_JSON"];
        if (string.IsNullOrWhiteSpace(jsonMap))
        {
            return null;
        }

        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMap);
            return map is not null && map.TryGetValue(keyId, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset parsed)
    {
        if (long.TryParse(value, out var epoch))
        {
            parsed = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }

        return DateTimeOffset.TryParse(value, out parsed);
    }

    private static byte[] DecodeSecret(string secret)
    {
        try
        {
            return Convert.FromBase64String(secret);
        }
        catch
        {
            return Encoding.UTF8.GetBytes(secret);
        }
    }
}
