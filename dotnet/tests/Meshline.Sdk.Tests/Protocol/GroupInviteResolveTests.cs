using Meshline.Tests.Support;
using Meshline.Validation;

namespace Meshline.Tests.Protocol;

public sealed class GroupInviteResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, null, 0L, true)]
    [InlineData(false, null, long.MaxValue, true)]
    [InlineData(false, null, -1L, false)]
    [InlineData(true, null, 0L, true)]
    [InlineData(true, null, 1L, true)]
    [InlineData(true, null, 2L, false)]
    [InlineData(false, 3L, 3L, true)]
    [InlineData(false, 3L, 4L, false)]
    public async Task Standalone_result_preserves_invitation_use_limits(bool targeted, long? maximum, long uses, bool valid)
    {
        using var scope = Clock.Use(new ManualClock());
        using var account = new AccountSigner();
        var result = await ProtocolResults.CreateGroupInviteAsync(account, Token);
        result = result with
        {
            Invite = result.Invite with
            {
                Invitee = targeted ? account.AccountId : null,
                MaxUses = maximum
            },
            Uses = uses
        };
        var violation = result.Validate(TestNetwork.Context);
        if (valid)
            Assert.Null(violation);
        else
            Assert.Equal(new ProtocolViolation(ProtocolViolationKind.Format, "The group invitation has invalid identity or usage fields."), violation);
    }
}
