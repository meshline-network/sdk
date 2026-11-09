using Org.BouncyCastle.Crypto.Generators;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Backups;

// Fixed by the SDK backup format, independently of wallet password derivation.
internal static class BackupPassword
{
    const int Cost = 65536;
    const int BlockSize = 8;
    const int Parallelism = 2;
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly SemaphoreSlim DerivationGate = new(1, 1);

    internal sealed record Parameters(string Algorithm, string Salt, int N, int R, int P);

    internal static Parameters CreateParameters() => new("scrypt", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), Cost, BlockSize, Parallelism);

    static byte[] Validate(Parameters parameters)
    {
        if (parameters is null || parameters.Algorithm != "scrypt" || parameters.N != Cost || parameters.R != BlockSize || parameters.P != Parallelism)
            throw new InvalidDataException("Unsupported backup password derivation parameters.");
        if (parameters.Salt is not { Length: 24 })
            throw new InvalidDataException("Invalid backup password salt.");
        Span<byte> salt = stackalloc byte[16];
        if (!Convert.TryFromBase64String(parameters.Salt, salt, out var length) || length != 16 || Convert.ToBase64String(salt) != parameters.Salt)
            throw new InvalidDataException("Invalid backup password salt.");
        return salt.ToArray();
    }

    internal static async Task<byte[]> DeriveAsync(string password, Parameters parameters, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        // Validate untrusted work factors before allocating or scheduling expensive work.
        var salt = Validate(parameters);
        await DerivationGate.WaitAsync(token).ConfigureAwait(false);
        byte[]? encoded = null;
        byte[]? key = null;
        try
        {
            token.ThrowIfCancellationRequested();
            encoded = Utf8.GetBytes(password.Normalize(NormalizationForm.FormC));
            // SCrypt is synchronous. Await it even after cancellation so memory and worker lifetime stay bounded.
            key = await Task.Run(() => SCrypt.Generate(encoded, salt, Cost, BlockSize, Parallelism, 32), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = key;
            key = null;
            return result;
        }
        finally
        {
            if (encoded is not null)
                CryptographicOperations.ZeroMemory(encoded);
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            DerivationGate.Release();
        }
    }
}
