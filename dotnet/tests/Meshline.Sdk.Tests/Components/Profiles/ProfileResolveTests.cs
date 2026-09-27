using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Profiles;

public sealed class ProfileResolveTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("valid")]
    [InlineData("profile_account")]
    [InlineData("certificate_account")]
    [InlineData("signature")]
    public async Task Resolved_profiles_require_matching_accounts_and_valid_signatures_before_caching(string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        using var otherAccount = new AccountSigner();
        var profile = new AccountProfile
        {
            Account = scenario == "profile_account" ? otherAccount.AccountId : fixture.Account.AccountId,
            Nickname = "Alice",
            PublicDiscovery = true,
            UpdatedAt = 0,
            DeviceSignature = []
        };
        profile = profile with
        {
            DeviceSignature = scenario == "signature" ? [.. new byte[64]] : [.. await fixture.Client.DeviceManager.SignAsync(profile.GetSigningInput(TestNetwork.Context), Token)]
        };
        var certificate = scenario == "certificate_account" ? new DeviceSigner(otherAccount, fixture.Relay.Clock).Certificate : fixture.Client.Device!;
        var result = new ProfileResolveResult
        {
            Profile = profile,
            SignerCertificate = certificate
        };

        Assert.Null(result.Validate(TestNetwork.Context));

        fixture.Relay.Profiles[fixture.Account.AccountId] = result;
        var manager = fixture.Client.ProfileManager;
        if (scenario == "valid")
        {
            var actual = await manager.GetProfileAsync(cancellationToken: Token);

            Assert.Equal(profile.ToJson(), actual!.ToJson());

            await using var database = fixture.Database.Open();

            Assert.Equal(profile.ToJson(), (await database.AccountProfiles.SingleAsync(Token)).DocumentJson);
        }
        else
        {
            if (scenario == "signature")
            {
                var error = await Assert.ThrowsAsync<CryptographicException>(() => manager.GetProfileAsync(cancellationToken: Token));

                Assert.Equal("The profile device signature is invalid.", error.Message);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetProfileAsync(cancellationToken: Token));

                Assert.Equal("The profile or signing certificate belongs to another account.", error.Message);
            }

            Assert.Null(manager.Profile);

            await using var database = fixture.Database.Open();

            Assert.Empty(await database.AccountProfiles.ToListAsync(Token));
        }
    }
}
