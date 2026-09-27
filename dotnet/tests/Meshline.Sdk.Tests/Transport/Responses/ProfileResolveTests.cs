using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class ProfileResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "profile")]
    [InlineData(true, "profile")]
    [InlineData(false, "certificate")]
    [InlineData(true, "certificate")]
    public async Task Typed_profile_responses_validate_nested_models_on_both_transports(bool webSocket, string scenario)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var result = await ProtocolResults.CreateProfileAsync(account, Token);
        result = scenario switch
        {
            "profile" => result with
            {
                Profile = result.Profile with
                {
                    UpdatedAt = -1
                }
            },
            "certificate" => result with
            {
                SignerCertificate = result.SignerCertificate with
                {
                    DeviceSignature = [.. new byte[64]]
                }
            },
            _ => result
        };
        var json = result.ToJson();
        var response = ResponseTransport.SendAsync<ProfileResolveResult>(client, relay, webSocket, HttpMethod.Get, "profile.resolve", json, request => Assert.Equal("profile.resolve", request.Method), Token);
        if (scenario == "valid")
            Assert.Equal(json, (await response.WaitAsync(TimeSpan.FromSeconds(10), Token)).ToJson());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal(scenario == "profile" ? "The profile update time must be nonnegative." : "The device signature is invalid.", error.Message);
        }
    }
}
