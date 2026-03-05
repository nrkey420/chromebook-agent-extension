using ChromeCollector.FunctionApp.Models;
using ChromeCollector.FunctionApp.Services;
using FluentAssertions;

namespace ChromeCollector.FunctionApp.Tests;

public class BatchSchemaValidatorTests
{
    [Fact]
    public void TryValidate_ReturnsFalse_WhenEventTypeMissing()
    {
        var batch = new ChromeBatch
        {
            Events =
            [
                new ChromeEvent { EventType = null, EventTimeUtc = DateTimeOffset.UtcNow }
            ]
        };

        var isValid = BatchSchemaValidator.TryValidate(batch, out var error);

        isValid.Should().BeFalse();
        error.Should().Contain("EventType");
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenEventTimeMissing()
    {
        var batch = new ChromeBatch
        {
            Events =
            [
                new ChromeEvent { EventType = "NAVIGATION", EventTimeUtc = null }
            ]
        };

        var isValid = BatchSchemaValidator.TryValidate(batch, out var error);

        isValid.Should().BeFalse();
        error.Should().Contain("EventTime");
    }
}
