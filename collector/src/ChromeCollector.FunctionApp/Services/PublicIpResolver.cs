using System.Net;
using Microsoft.Azure.Functions.Worker.Http;

namespace ChromeCollector.FunctionApp.Services;

public interface IPublicIpResolver
{
    string? Resolve(HttpRequestData request);
}

public sealed class PublicIpResolver : IPublicIpResolver
{
    // Headers set by the Azure platform front end, in order of preference.
    // X-Forwarded-For is only read for its first (client-most) entry.
    private static readonly string[] HeaderOrder = ["X-Azure-ClientIP", "X-Client-IP", "X-Forwarded-For"];

    public string? Resolve(HttpRequestData request)
    {
        foreach (var header in HeaderOrder)
        {
            if (!request.Headers.TryGetValues(header, out var values)) continue;
            var ip = Parse(values.FirstOrDefault());
            if (ip is not null) return ip;
        }

        return null;
    }

    /// <summary>Extracts the first address from a header value, stripping any port ("1.2.3.4:5678", "[::1]:443").</summary>
    public static string? Parse(string? headerValue)
    {
        var first = headerValue?.Split(',').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(first)) return null;

        if (first.StartsWith('['))
        {
            var end = first.IndexOf(']');
            if (end > 0) first = first[1..end];
        }
        else if (first.Count(c => c == ':') == 1)
        {
            first = first[..first.IndexOf(':')];
        }

        return IPAddress.TryParse(first, out var address) ? address.ToString() : null;
    }
}
