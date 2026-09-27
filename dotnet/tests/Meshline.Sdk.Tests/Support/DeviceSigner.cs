using Meshline.Interactions;
using Meshline.Models.Protocol;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Meshline.Tests.Support;

internal sealed class DeviceSigner : IDeviceSigner
{
    readonly byte[] key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    public DeviceCertificate Certificate { get; }

    public DeviceSigner(AccountSigner account, TimeProvider clock)
    {
        var signing = new byte[32];
        Ed25519.GeneratePublicKey(key.AsSpan(), signing.AsSpan());
        var encryption = new byte[32];
        Org.BouncyCastle.Math.EC.Rfc7748.X25519.GeneratePublicKey(key.AsSpan(), encryption.AsSpan());
        var certificate = new DeviceCertificate
        {
            Account = account.AccountId,
            AccountPublicKey = account.PublicKey,
            SigningPublicKey = [.. signing],
            EncryptionPublicKey = [.. encryption],
            NotBefore = clock.GetUtcNow().ToUnixTimeSeconds() - 60,
            ExpiresAt = clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds(),
            DeviceSignature = [],
            AccountSignature = []
        };
        certificate = certificate with
        {
            DeviceSignature = [.. Sign(certificate.GetDeviceSigningInput(TestNetwork.Context))]
        };
        Certificate = certificate with
        {
            AccountSignature = [.. account.Sign(certificate.GetAccountSigningInput(TestNetwork.Context))]
        };
    }

    byte[] Sign(ReadOnlySpan<byte> data)
    {
        var result = new byte[64];
        Ed25519.Sign(key.AsSpan(), data, result.AsSpan());
        return result;
    }

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Sign(data.Span));
    }
}
