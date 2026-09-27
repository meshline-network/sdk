using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;

namespace Meshline.Tests.Protocol;

public sealed class GroupRotationPrepareTests
{
    [Fact]
    public void Preparation_expires_at_the_current_clock_boundary()
    {
        var clock = new ManualClock();
        using var scope = Clock.Use(clock);
        var result = new GroupRotationPrepareResult
        {
            Prepared = 0,
            ExpiresAt = clock.GetUtcNow().ToUnixTimeSeconds() + 1
        };

        Assert.Null(result.Validate());

        clock.Advance(TimeSpan.FromSeconds(1));
        var expected = new ProtocolViolation(ProtocolViolationKind.Time, "The relay returned an invalid or changed rotation preparation interval.");

        Assert.Equal(expected, result.Validate());

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(expected, result.Validate(TestNetwork.Context));
    }
}
