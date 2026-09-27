using Meshline.Models.Protocol;
using Meshline.Validation;

namespace Meshline.Tests.Protocol;

public sealed class GroupRecoverySubmitTests
{
    [Theory]
    [InlineData(0L, 1L, true)]
    [InlineData(-1L, 1L, false)]
    [InlineData(1L, 1L, false)]
    [InlineData(2L, 1L, false)]
    [InlineData(long.MaxValue - 1, long.MaxValue, true)]
    public void Acceptance_interval_preserves_existing_boundaries(long acceptedAt, long expiresAt, bool valid)
    {
        var result = new GroupRecoverySubmitResult
        {
            AcceptedAt = acceptedAt,
            ExpiresAt = expiresAt
        };
        var violation = result.Validate();
        if (valid)
            Assert.Null(violation);
        else
            Assert.Equal(new ProtocolViolation(ProtocolViolationKind.Time, "The relay returned an invalid recovery acceptance interval."), violation);
    }
}
