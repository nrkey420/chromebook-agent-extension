using ChromeCollector.FunctionApp.Models;

namespace ChromeCollector.FunctionApp.Services;

/// <summary>
/// Derives Domain and search terms from the URL and clamps fields to the SQL column sizes,
/// so one oversized value cannot fail a whole batch. Done server-side so parsing rules can
/// change without redeploying the extension.
/// </summary>
public static class EventEnricher
{
    private sealed record SearchEngineRule(string Name, Func<string, bool> HostMatches, string PathPrefix, string QueryParameter);

    private static readonly SearchEngineRule[] SearchEngines =
    [
        new("Google", h => h.StartsWith("google.") || h.Contains(".google."), "/search", "q"),
        new("Bing", h => h == "bing.com" || h.EndsWith(".bing.com"), "/search", "q"),
        new("DuckDuckGo", h => h == "duckduckgo.com", "/", "q"),
        new("Yahoo", h => h == "search.yahoo.com", "/search", "p"),
        new("YouTube", h => h == "youtube.com" || h == "m.youtube.com", "/results", "search_query"),
        new("Ecosia", h => h == "ecosia.org", "/search", "q"),
        new("Brave", h => h == "search.brave.com", "/search", "q"),
    ];

    public static void Enrich(ChromeEvent e)
    {
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var host = NormalizeHost(uri.Host);
            e.Domain ??= host;

            if (e.SearchQuery is null)
            {
                var rule = SearchEngines.FirstOrDefault(r => r.HostMatches(host) && uri.AbsolutePath.StartsWith(r.PathPrefix, StringComparison.OrdinalIgnoreCase));
                var query = rule is null ? null : GetQueryParameter(uri.Query, rule.QueryParameter);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    e.SearchEngine = rule!.Name;
                    e.SearchQuery = query.Trim();
                }
            }
        }

        e.Url = Clamp(e.Url, 2048);
        e.Domain = Clamp(e.Domain, 256);
        e.Title = Clamp(e.Title, 1024);
        e.SearchQuery = Clamp(e.SearchQuery, 1024);
        e.Transition = Clamp(e.Transition, 64);
        e.Detail = Clamp(e.Detail, 1024);
        e.DownloadFileName = Clamp(e.DownloadFileName, 512);
        e.DownloadMime = Clamp(e.DownloadMime, 256);
        e.DownloadDanger = Clamp(e.DownloadDanger, 64);
        e.DownloadState = Clamp(e.DownloadState, 64);
        e.EventType = Clamp(e.EventType?.Trim().ToUpperInvariant(), 32);
        e.UserEmail = Clamp(e.UserEmail?.Trim().ToLowerInvariant(), 320);
        e.DirectoryDeviceId = Clamp(e.DirectoryDeviceId, 128);
        e.DeviceSerial = Clamp(e.DeviceSerial, 128);
        e.AssetId = Clamp(e.AssetId, 256);
        e.AnnotatedLocation = Clamp(e.AnnotatedLocation, 256);
        e.Hostname = Clamp(e.Hostname, 256);
        e.Manufacturer = Clamp(e.Manufacturer, 256);
        e.Model = Clamp(e.Model, 256);
        e.ChromeVersion = Clamp(e.ChromeVersion, 64);
        e.PlatformVersion = Clamp(e.PlatformVersion, 64);
        e.InternalIp = Clamp(e.InternalIp, 64);
        e.InternalIpv6 = Clamp(e.InternalIpv6, 64);
        e.MacAddress = Clamp(e.MacAddress, 64);
        e.ExtensionVersion = Clamp(e.ExtensionVersion, 32);
    }

    private static string NormalizeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static string? GetQueryParameter(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal)) continue;
            var value = idx < 0 ? string.Empty : pair[(idx + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }

    private static string? Clamp(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
