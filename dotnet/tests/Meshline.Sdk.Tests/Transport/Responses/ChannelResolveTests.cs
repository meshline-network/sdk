using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class ChannelResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "descriptor")]
    [InlineData(true, "descriptor")]
    [InlineData(false, "certificate")]
    [InlineData(true, "certificate")]
    public async Task Typed_channel_responses_validate_nested_models_on_both_transports(bool webSocket, string scenario)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var result = await ProtocolResults.CreateChannelAsync(account, relay, Token);
        result = scenario switch
        {
            "descriptor" => result with
            {
                Descriptor = result.Descriptor with
                {
                    Name = " "
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
        var response = ResponseTransport.SendAsync<ChannelResolveResult>(client, relay, webSocket, HttpMethod.Get, "channel.resolve", json, request => Assert.Equal("channel.resolve", request.Method), Token);
        if (scenario == "valid")
            Assert.Equal(json, (await response.WaitAsync(TimeSpan.FromSeconds(10), Token)).ToJson());
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal(
                scenario == "descriptor" ? "The channel name must contain non-whitespace text and cannot exceed 256 UTF-8 bytes." : "The device signature is invalid.",
                error.Message);
        }
    }
}
