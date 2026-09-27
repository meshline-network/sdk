using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class GroupRotationPrepareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Typed_preparation_responses_are_validated_on_both_transports(bool webSocket, bool valid)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var result = new GroupRotationPrepareResult
        {
            Prepared = 1,
            ExpiresAt = Clock.UtcNow.ToUnixTimeSeconds() + (valid ? 600 : 0)
        };
        var json = result.ToJson();
        var response = ResponseTransport.SendAsync<GroupRotationPrepareResult>(
            client,
            relay,
            webSocket,
            HttpMethod.Patch,
            "group.secret.rotation.prepare",
            json,
            request => Assert.Equal("group.secret.rotation.prepare", request.Method),
            Token);
        if (valid)
            Assert.Equal(json, (await response.WaitAsync(TimeSpan.FromSeconds(10), Token)).ToJson());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal("The relay returned an invalid or changed rotation preparation interval.", error.Message);
        }
    }
}
