using Meshline.Tests.Support;

namespace Meshline.Tests.Protocol;

public sealed class ProfileResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nested_validation_requires_context_and_reports_profile_before_certificate()
    {
        using var scope = Clock.Use(new ManualClock());
        using var account = new AccountSigner();
        var result = await ProtocolResults.CreateProfileAsync(account, Token);

        Assert.Null(result.Validate(TestNetwork.Context));
        Assert.Throws<ArgumentNullException>(() => result.Validate(null));

        result = result with
        {
            Profile = result.Profile with
            {
                UpdatedAt = -1
            },
            SignerCertificate = result.SignerCertificate with
            {
                SigningPublicKey = []
            }
        };

        Assert.Equal(result.Profile.Validate(), result.Validate(TestNetwork.Context));
    }
}
