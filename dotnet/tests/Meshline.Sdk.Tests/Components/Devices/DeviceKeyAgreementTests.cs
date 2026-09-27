using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Meshline.Tests.Components.Devices;

public sealed class DeviceKeyAgreementTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_key_agreement_matches_fixed_sender_and_recipient_vectors(bool sender)
    {
        using var time = Clock.Use(new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1730000000)));
        await using var fixture = EncryptionVectorSetup.CreateClient();
        var vector = ProtocolVectors.Read("message-encryption").GetProperty("timeline");
        var request = ProtocolModel.FromJson<MessageSendRequest>(vector.GetProperty("request").GetRawText())!;
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(vector.GetProperty("request").GetProperty("signer_certificate").GetRawText())!;
        var signingKey = ProtocolVectors.Decode(vector.GetProperty("sender_signing_private_key").GetString()!);
        var encryptionKey = ProtocolVectors.Decode(vector.GetProperty(sender ? "sender_encryption_private_key" : "target_encryption_private_key").GetString()!);
        if (!sender)
        {
            // The recipient box vector uses a synthetic device ID without a certificate.
            // Bind its real X25519 key to a valid certificate for the public key API.
            var publicKey = new byte[32];
            X25519.GeneratePublicKey(encryptionKey.AsSpan(), publicKey.AsSpan());
            certificate = certificate with
            {
                EncryptionPublicKey = [.. publicKey]
            };
            var signature = new byte[64];
            Ed25519.Sign(signingKey.AsSpan(), certificate.GetDeviceSigningInput(TestNetwork.Context), signature.AsSpan());
            certificate = certificate with
            {
                DeviceSignature = [.. signature]
            };
            certificate = certificate with
            {
                AccountSignature = [.. fixture.Account.Sign(certificate.GetAccountSigningInput(TestNetwork.Context))]
            };
        }

        Assert.Equal(fixture.Account.AccountId, certificate.Account);
        Assert.Null(certificate.Validate(TestNetwork.Context));

        await EncryptionVectorSetup.SeedDeviceAsync(fixture, certificate, signingKey, encryptionKey, Token);
        await fixture.InitializeAsync(establish: false);
        var box = sender ? request.SenderBoxes!.Value[0] : request.RecipientBoxes[0];
        var actual = await fixture.Client.DeviceManager.DeriveSharedSecretAsync(box.Enc.AsMemory(), Token);

        Assert.Equal(ProtocolVectors.Decode(vector.GetProperty(sender ? "sender_key_box" : "target_key_box").GetProperty("shared_secret").GetString()!), actual);
    }
}
