using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class GroupRecoverySubmitTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Typed_recovery_submit_responses_are_validated_on_both_transports(bool webSocket, bool valid)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var result = new GroupRecoverySubmitResult
        {
            AcceptedAt = 0,
            ExpiresAt = valid ? 1 : 0
        };
        var json = result.ToJson();
        var response = ResponseTransport.SendAsync<GroupRecoverySubmitResult>(
            client,
            relay,
            webSocket,
            HttpMethod.Post,
            "group.member.recovery.submit",
            json,
            request => Assert.Equal("group.member.recovery.submit", request.Method),
            Token);
        if (valid)
            Assert.Equal(json, (await response.WaitAsync(TimeSpan.FromSeconds(10), Token)).ToJson());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal("The relay returned an invalid recovery acceptance interval.", error.Message);
        }
    }
}
