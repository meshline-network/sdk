using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class GroupStateTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Typed_group_state_responses_are_validated_on_both_transports(bool webSocket, bool valid)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var state = new GroupState
        {
            GroupId = "grp_" + new string('A', 43),
            Name = valid ? "preview" : " ",
            Owner = account.AccountId,
            MemberCount = 1,
            MemberCapacity = 10,
            Status = GroupStatus.Active,
            InvitePolicy = GroupInvitePolicy.Administrators
        };
        var json = state.ToJson();
        var response = ResponseTransport.SendAsync<GroupState>(client, relay, webSocket, HttpMethod.Get, "group.resolve", json, request => Assert.Equal("group.resolve", request.Method), Token);
        if (valid)
            Assert.Equal(json, (await response.WaitAsync(TimeSpan.FromSeconds(10), Token)).ToJson());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal("The relay returned an invalid group preview.", error.Message);
        }
    }
}
