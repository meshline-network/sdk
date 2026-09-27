using Meshline.Tests.Support;

namespace Meshline.Tests.Protocol;

public sealed class ChannelResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nested_validation_requires_context_and_reports_descriptor_before_certificate()
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var result = await ProtocolResults.CreateChannelAsync(account, relay, Token);

        Assert.Null(result.Validate(TestNetwork.Context));
        Assert.Throws<ArgumentNullException>(() => result.Validate(null));

        result = result with
        {
            Descriptor = result.Descriptor with
            {
                Name = " "
            },
            SignerCertificate = result.SignerCertificate with
            {
                SigningPublicKey = []
            }
        };

        Assert.Equal(result.Descriptor.Validate(TestNetwork.Context), result.Validate(TestNetwork.Context));
    }
}
