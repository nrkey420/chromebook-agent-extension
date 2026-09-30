using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;

namespace ChromeCollector.FunctionApp.Tests;

public class CollectorHelperTests
{
    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("203.0.113.5:51234", "203.0.113.5")]
    [InlineData("203.0.113.5, 10.0.0.1", "203.0.113.5")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("not-an-ip", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PublicIpResolver_Parse_HandlesPortsAndLists(string? header, string? expected)
    {
        PublicIpResolver.Parse(header).Should().Be(expected);
    }

    [Theory]
    [InlineData("key1", "key1")]
    [InlineData("../../etc", "_.._etc")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("", "unknown")]
    public void BlobWriter_SafeSegment_RemovesPathCharacters(string input, string expected)
    {
        BlobWriter.SafeSegment(input).Should().Be(expected);
    }

    [Fact]
    public void EventEnricher_DerivesDomainAndNormalizesFields()
    {
        var e = new ChromeEvent { EventType = " navigation ", UserEmail = " Student@District.ORG ", Url = "https://www.Example.com/a?b=c" };

        EventEnricher.Enrich(e);

        e.Domain.Should().Be("example.com");
        e.EventType.Should().Be("NAVIGATION");
        e.UserEmail.Should().Be("student@district.org");
    }

    [Fact]
    public void EventEnricher_ClampsOversizedValuesToColumnSizes()
    {
        var e = new ChromeEvent { EventType = "NAVIGATION", Url = "https://example.com/" + new string('a', 5000), Title = new string('t', 5000) };

        EventEnricher.Enrich(e);

        e.Url!.Length.Should().Be(2048);
        e.Title!.Length.Should().Be(1024);
    }

    [Fact]
    public void EventEnricher_IgnoresNonWebUrls()
    {
        var e = new ChromeEvent { EventType = "NAVIGATION", Url = "chrome://settings" };

        EventEnricher.Enrich(e);

        e.Domain.Should().BeNull();
    }

    [Fact]
    public void RateLimiter_DefaultAllowsBurstOfThirtyPerClient()
    {
        var limiter = new InMemoryTokenBucketRateLimiter();
        Enumerable.Range(0, 30).All(_ => limiter.TryConsume("key:device-a")).Should().BeTrue();
        limiter.TryConsume("key:device-a").Should().BeFalse();
        limiter.TryConsume("key:device-b").Should().BeTrue();
    }
}
