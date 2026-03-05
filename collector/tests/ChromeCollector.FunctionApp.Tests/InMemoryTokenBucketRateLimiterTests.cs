using ChromeCollector.FunctionApp.Services;
using FluentAssertions;

namespace ChromeCollector.FunctionApp.Tests;

public class InMemoryTokenBucketRateLimiterTests
{
    [Fact]
    public void TryConsume_ReturnsFalse_AfterCapacityIsExceeded()
    {
        var limiter = new InMemoryTokenBucketRateLimiter();
        var keyId = "tenant-a";

        var attempts = Enumerable.Range(0, 31).Select(_ => limiter.TryConsume(keyId)).ToList();

        attempts.Count(x => x).Should().Be(30);
        attempts.Last().Should().BeFalse();
    }

    [Fact]
    public void TryConsume_UsesPerKeyBuckets()
    {
        var limiter = new InMemoryTokenBucketRateLimiter();

        for (var i = 0; i < 30; i++)
        {
            limiter.TryConsume("key-1").Should().BeTrue();
        }

        limiter.TryConsume("key-1").Should().BeFalse();
        limiter.TryConsume("key-2").Should().BeTrue();
    }
}
