using Meshline.Models.Protocol;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;

namespace Meshline.Tests.Support;

internal static class DeviceCertificateSetup
{
    internal static DeviceCertificate Certificate(TestClient fixture, bool expired = false)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signing = new byte[32];
        var encryption = new byte[32];
        Ed25519.GeneratePublicKey(key, signing);
        X25519.GeneratePublicKey(key, encryption);
        var now = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds();
        var certificate = new DeviceCertificate
        {
            Account = fixture.Account.AccountId,
            AccountPublicKey = fixture.Account.PublicKey,
            SigningPublicKey = [.. signing],
            EncryptionPublicKey = [.. encryption],
            NotBefore = now - (expired ? 172800 : 1),
            ExpiresAt = now + (expired ? -1 : 86400),
            DeviceSignature = [],
            AccountSignature = []
        };
        var signature = new byte[64];
        Ed25519.Sign(key, certificate.GetDeviceSigningInput(TestNetwork.Context), signature);
        certificate = certificate with { DeviceSignature = [.. signature] };
        CryptographicOperations.ZeroMemory(key);
        return certificate with { AccountSignature = [.. fixture.Account.Sign(certificate.GetAccountSigningInput(TestNetwork.Context))] };
    }
}
