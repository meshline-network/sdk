using Meshline.Identity;
using Meshline.Interactions;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Meshline.Tests.Support;

internal sealed class AccountSigner : IAccountSigner, IDisposable
{
    readonly ECDsa key;
    public string AccountId { get; }
    public ImmutableArray<byte> PublicKey { get; }

    public AccountSigner(byte[]? privateKey = null)
    {
        key = privateKey is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey
        });
        var q = key.ExportParameters(false).Q;
        PublicKey = [(byte)(2 + (q.Y![^1] & 1)), .. q.X!];
        AccountId = AccountAdapter.GetAccountId("neo:860833102", PublicKey.AsSpan());
    }

    internal byte[] Sign(ReadOnlySpan<byte> data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Sign(data.Span));
    }

    public void Dispose() => key.Dispose();
}
