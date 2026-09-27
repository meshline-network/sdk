using Meshline.Interactions;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Tests.Support;

internal sealed class SecretProtector : ISecretProtector
{
    readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    internal bool Fail { get; set; }

    public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail)
            throw new IOException("Injected secret protection failure");
        var output = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plaintext.Span, output.AsSpan(12, plaintext.Length), output.AsSpan(12 + plaintext.Length), Encoding.UTF8.GetBytes(purpose));
        return Task.FromResult(output);
    }

    public Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> protectedData, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail)
            throw new IOException("Injected secret protection failure");
        var output = new byte[protectedData.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(protectedData.Span[..12], protectedData.Span.Slice(12, output.Length), protectedData.Span[^16..], output, Encoding.UTF8.GetBytes(purpose));
        return Task.FromResult(output);
    }
}
