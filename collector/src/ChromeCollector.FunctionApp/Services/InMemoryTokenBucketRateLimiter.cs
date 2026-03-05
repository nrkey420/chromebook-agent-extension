using System.Collections.Concurrent;

namespace ChromeCollector.FunctionApp.Services;

public interface IRequestRateLimiter
{
    bool TryConsume(string keyId);
}

public sealed class InMemoryTokenBucketRateLimiter : IRequestRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens { get; set; }
        public DateTimeOffset LastRefillUtc { get; set; }
    }

    private const int Capacity = 30;
    private const int RefillRatePerSecond = 10;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public bool TryConsume(string keyId)
    {
        var now = DateTimeOffset.UtcNow;
        var bucket = _buckets.GetOrAdd(keyId, _ => new Bucket
        {
            Tokens = Capacity,
            LastRefillUtc = now
        });

        lock (bucket)
        {
            var elapsedSeconds = Math.Max(0, (now - bucket.LastRefillUtc).TotalSeconds);
            bucket.Tokens = Math.Min(Capacity, bucket.Tokens + (elapsedSeconds * RefillRatePerSecond));
            bucket.LastRefillUtc = now;

            if (bucket.Tokens < 1)
            {
                return false;
            }

            bucket.Tokens -= 1;
            return true;
        }
    }
}
