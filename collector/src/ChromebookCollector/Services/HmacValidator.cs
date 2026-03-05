using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ChromebookCollector.Services;

public sealed class HmacValidator
{
    private readonly CollectorOptions _options;

    public HmacValidator(IOptions<CollectorOptions> options)
    {
        _options = options.Value;
    }

    public bool Validate(string keyId, string timestampHeader, string signatureHeader, string body)
    {
        if (!_options.HmacKeys.TryGetValue(keyId, out var secretBase64)) return false;
        if (!long.TryParse(timestampHeader, out var unixSeconds)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > _options.HmacAllowedSkewSeconds) return false;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secretBase64);
        }
        catch
        {
            return false;
        }

        using var hmac = new HMACSHA256(key);
        var payload = Encoding.UTF8.GetBytes($"{timestampHeader}\n{body}");
        var computed = Convert.ToBase64String(hmac.ComputeHash(payload));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signatureHeader));
    }
}
