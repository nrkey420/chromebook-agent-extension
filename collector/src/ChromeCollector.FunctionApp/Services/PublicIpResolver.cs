using Microsoft.Azure.Functions.Worker.Http;

namespace ChromeCollector.FunctionApp.Services;

public interface IPublicIpResolver
{
    string? Resolve(HttpRequestData request);
}

public sealed class PublicIpResolver : IPublicIpResolver
{
    public string? Resolve(HttpRequestData request)
    {
        // PoC safety: trust platform-forwarded address only.
        if (request.Headers.TryGetValues("X-Azure-ClientIP", out var values))
        {
            return values.FirstOrDefault();
        }

        return null;
    }
}
