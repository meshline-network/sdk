using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class GroupInviteResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "invite")]
    [InlineData(true, "invite")]
    [InlineData(false, "certificate")]
    [InlineData(true, "certificate")]
    [InlineData(false, "uses")]
    [InlineData(true, "uses")]
    public async Task Typed_invitation_responses_are_validated_on_both_transports(bool webSocket, string scenario)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var result = await ProtocolResults.CreateGroupInviteAsync(account, Token);
        result = scenario switch
        {
            "invite" => result with
            {
                Invite = result.Invite with
                {
                    ExpiresAt = Clock.UtcNow.ToUnixTimeSeconds()
                }
            },
            "certificate" => result with
            {
                SignerCertificate = result.SignerCertificate with
                {
                    DeviceSignature = [.. new byte[64]]
                }
            },
            "uses" => result with
            {
                Uses = -1
            },
            _ => result
        };
        var json = result.ToJson();
        var response = ResponseTransport.SendAsync<GroupInviteResolveResult>(client, relay, webSocket, HttpMethod.Get, "group.invite.resolve", json, request => Assert.Equal("group.invite.resolve", request.Method), Token);
        if (scenario == "valid")
            Assert.Equal(json, (await response.WaitAsync(TimeSpan.FromSeconds(10), Token)).ToJson());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal(scenario switch
            {
                "invite" => "The group invitation has expired.",
                "certificate" => "The device signature is invalid.",
                _ => "The group invitation has invalid identity or usage fields."
            }, error.Message);
        }
    }
}
