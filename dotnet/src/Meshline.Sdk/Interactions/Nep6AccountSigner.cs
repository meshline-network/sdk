using Meshline.Identity;
using Meshline.Models;
using Org.BouncyCastle.Crypto.Generators;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Interactions;

/// <summary>Signs Meshline account input with a Neo N3 single-signature account from a NEP-6 wallet.</summary>
/// <remarks>The caller owns this signer and must dispose it. Loading never changes the wallet or registers DI services.</remarks>
public sealed class Nep6AccountSigner : IAccountSigner, IDisposable
{
    readonly ECDsa signer;
    readonly Lock signingLock = new();
    bool disposed;

    /// <inheritdoc cref="IAccountSigner.AccountId"/>
    public string AccountId { get; }
    /// <inheritdoc cref="IAccountSigner.PublicKey"/>
    public ImmutableArray<byte> PublicKey { get; }
    /// <summary>The selected Neo N3 address.</summary>
    public string Address { get; }

    Nep6AccountSigner(ECDsa signer, NetworkContext context)
    {
        this.signer = signer;
        var point = signer.ExportParameters(false).Q;
        PublicKey = [(byte)(2 | (point.Y![^1] & 1)), .. point.X!];
        AccountId = AccountAdapter.GetAccountId($"neo:{context.Reference}", PublicKey.AsSpan());
        Address = AccountId[(AccountId.LastIndexOf(':') + 1)..];
    }

    /// <summary>Loads an existing wallet file, selecting its first account unless an index is supplied.</summary>
    /// <param name="path">The wallet file path, chosen by the application.</param>
    /// <param name="password">The wallet password, normalized to NFC for NEP-2 decryption.</param>
    /// <param name="context">The trusted network used in the account identifier.</param>
    /// <param name="accountIndex">A zero-based index in file order; no fallback to another account occurs.</param>
    /// <returns>An unlocked signer owned by the caller.</returns>
    public static Nep6AccountSigner Load(string path, string password, NetworkContext context, int accountIndex = 0) =>
        Parse(File.ReadAllText(path), password, context, accountIndex);

    /// <summary>Loads wallet JSON supplied by the application without requiring filesystem access.</summary>
    /// <param name="walletJson">Neo N3 NEP-6 wallet JSON.</param>
    /// <param name="password">The wallet password, normalized to NFC for NEP-2 decryption.</param>
    /// <param name="context">The trusted network used in the account identifier.</param>
    /// <param name="accountIndex">A zero-based index in file order; defaults to the first account.</param>
    /// <returns>An unlocked signer owned by the caller.</returns>
    public static Nep6AccountSigner Parse(string walletJson, string password, NetworkContext context, int accountIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(walletJson);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(accountIndex);
        try
        {
            using var document = JsonDocument.Parse(walletJson, new JsonDocumentOptions { MaxDepth = 32 });
            var wallet = document.RootElement;
            if (wallet.GetProperty("version").GetString() != "1.0")
                throw new InvalidDataException("Expected a NEP-6 version 1.0 wallet.");
            var accounts = wallet.GetProperty("accounts");
            if (accounts.ValueKind != JsonValueKind.Array || accountIndex >= accounts.GetArrayLength())
                throw new InvalidDataException("The selected wallet account does not exist.");
            var account = accounts[accountIndex];
            var address = account.GetProperty("address").GetString();
            var encrypted = account.GetProperty("key").GetString();
            if (string.IsNullOrEmpty(encrypted))
                throw new InvalidDataException("The selected account has no encrypted private key.");
            var contract = account.GetProperty("contract");
            var parameters = contract.GetProperty("parameters");
            if (contract.GetProperty("deployed").ValueKind != JsonValueKind.False
                || parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 1
                || parameters[0].GetProperty("type").GetString() != "Signature")
                throw new InvalidDataException("Expected a Neo N3 standard single-signature account.");
            var script = Convert.FromBase64String(contract.GetProperty("script").GetString()!);
            var scrypt = wallet.GetProperty("scrypt");
            var n = scrypt.GetProperty("n").GetInt32();
            var r = scrypt.GetProperty("r").GetInt32();
            var p = scrypt.GetProperty("p").GetInt32();
            // Bound work from an imported file before allocating or deriving a key.
            if (n < 2 || (n & (n - 1)) != 0 || r < 1 || p < 1
                || 128m * r * (n + (long)p + 2) > 256L * 1024 * 1024 || (decimal)n * r * p > 16_777_216)
                throw new InvalidDataException("Unsupported NEP-6 scrypt parameters (256 MiB memory / 16777216 work limit).");
            return Decrypt(encrypted, password, context, address, script, n, r, p);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Malformed Neo N3 NEP-6 wallet.", error);
        }
    }

    static Nep6AccountSigner Decrypt(string encrypted, string password, NetworkContext context, string? address, byte[] script, int n, int r, int p)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        if (encrypted.Length != 58) throw new InvalidDataException("Invalid NEP-2 key length.");
        var value = BigInteger.Zero;
        foreach (var character in encrypted)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0) throw new InvalidDataException("Invalid NEP-2 Base58 character.");
            value = value * 58 + digit;
        }
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length != 43 || bytes[0] != 1 || bytes[1] != 0x42 || bytes[2] != 0xe0
            || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(39), SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, 39))).AsSpan(0, 4)))
            throw new InvalidDataException("Invalid NEP-2 prefix or checksum.");

        var passwordBytes = new UTF8Encoding(false, true).GetBytes(password.Normalize(NormalizationForm.FormC));
        byte[]? derived = null;
        byte[]? privateKey = null;
        byte[]? aesKey = null;
        ECDsa? key = null;
        try
        {
            derived = SCrypt.Generate(passwordBytes, bytes[3..7], n, r, p, 64);
            using var aes = Aes.Create();
            aesKey = derived[32..];
            aes.Key = aesKey;
            privateKey = aes.DecryptEcb(bytes.AsSpan(7, 32), PaddingMode.None);
            for (var i = 0; i < privateKey.Length; i++) privateKey[i] ^= derived[i];
            key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey });
            var result = new Nep6AccountSigner(key, context);
            if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(3, 4), SHA256.HashData(SHA256.HashData(Encoding.ASCII.GetBytes(result.Address))).AsSpan(0, 4)))
                throw new CryptographicException("The wallet password or encrypted key is incorrect.");
            ReadOnlySpan<byte> expectedScript = [0x0c, 0x21, .. result.PublicKey, 0x41, 0x56, 0xe7, 0xb3, 0x27];
            if (address != result.Address || !script.AsSpan().SequenceEqual(expectedScript))
                throw new InvalidDataException("The account address and standard contract must match its private key.");
            key = null; // Ownership transfers only after every identity check succeeds.
            return result;
        }
        finally
        {
            key?.Dispose();
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
            if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey);
            if (aesKey is not null) CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    /// <inheritdoc cref="IAccountSigner.SignAsync"/>
    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (signingLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(signer.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    /// <summary>Releases the decrypted signing key. The wallet file is unchanged.</summary>
    public void Dispose()
    {
        lock (signingLock)
        {
            if (disposed) return;
            disposed = true;
            signer.Dispose();
        }
    }
}
