using System.Collections.Concurrent;

namespace ChromeCollector.FunctionApp.Services;

public interface IRequestRateLimiter
{
    bool TryConsume(string clientKey);
}

/// <summary>
/// Per-client token bucket (per instance). The client key is keyId + device id, because the
/// whole fleet shares one HMAC key; limiting per key alone would throttle every device together.
/// </summary>
public sealed class InMemoryTokenBucketRateLimiter : IRequestRateLimiter
{
    private sealed class Bucket
    {
        public double Tokens { get; set; }
        public DateTimeOffset LastRefillUtc { get; set; }
    }

    private const int MaxTrackedClients = 200_000;

    private readonly int _capacity;
    private readonly double _refillPerSecond;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public InMemoryTokenBucketRateLimiter() : this(capacity: 30, refillPerSecond: 1) { }

    public InMemoryTokenBucketRateLimiter(int capacity, double refillPerSecond)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
    }

    public bool TryConsume(string clientKey)
    {
        var now = DateTimeOffset.UtcNow;
        if (_buckets.Count > MaxTrackedClients) _buckets.Clear();

        var bucket = _buckets.GetOrAdd(clientKey, _ => new Bucket { Tokens = _capacity, LastRefillUtc = now });

        lock (bucket)
        {
            var elapsedSeconds = Math.Max(0, (now - bucket.LastRefillUtc).TotalSeconds);
            bucket.Tokens = Math.Min(_capacity, bucket.Tokens + (elapsedSeconds * _refillPerSecond));
            bucket.LastRefillUtc = now;

            if (bucket.Tokens < 1) return false;

            bucket.Tokens -= 1;
            return true;
        }
    }
}
