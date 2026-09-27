using Meshline.Validation;
using Org.BouncyCastle.Crypto.Digests;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Meshline.Identity;

sealed partial class NeoAccountAdapter : AccountAdapter
{
    const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    static readonly BigInteger _prime = new(Convert.FromHexString("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF"), isUnsigned: true, isBigEndian: true);
    static readonly BigInteger _curveB = new(Convert.FromHexString("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B"), isUnsigned: true, isBigEndian: true);

    [GeneratedRegex(@"\Aneo:(0|[1-9][0-9]{0,9})\z", RegexOptions.CultureInvariant)]
    private static partial Regex NeoChainIdRegex { get; }

    protected override string Namespace => "neo";

    protected override string GetAccountIdCore(string chainId, ReadOnlySpan<byte> publicKey)
    {
        ValidateChainId(chainId);
        var address = EncodeAddress(GetScriptHash(publicKey));
        return $"{chainId}:{address}";
    }

    protected override ProtocolViolation? ValidateAccountIdCore(string accountId)
    {
        var separator = accountId.LastIndexOf(':');
        if (separator < 0)
            return new(ProtocolViolationKind.Format, "Expected a Neo N3 account identifier.");

        try
        {
            var chainId = accountId[..separator];
            ValidateChainId(chainId);
            ValidateAddress(accountId.AsSpan(separator + 1));
            return null;
        }
        catch (FormatException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
    }

    public override bool VerifySignature(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
            return false;

        var key = TryDecodePublicKey(publicKey);
        if (key is null)
            return false;

        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = key.Value });
        return verifier.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    static void ValidateChainId(string chainId)
    {
        var match = NeoChainIdRegex.Match(chainId);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new FormatException("Expected a canonical Neo N3 chain identifier.");
    }

    static ECPoint DecodePublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != 33 || publicKey[0] is not (0x02 or 0x03))
            throw new FormatException("Expected a compressed Neo N3 account public key.");

        var x = new BigInteger(publicKey[1..], isUnsigned: true, isBigEndian: true);
        if (x >= _prime)
            throw new FormatException("The public key coordinate is outside the P-256 field.");

        var square = (x * x * x - 3 * x + _curveB) % _prime;
        if (square.Sign < 0)
            square += _prime;

        // P-256's prime is 3 modulo 4, so this exponent recovers a square root.
        var y = BigInteger.ModPow(square, (_prime + 1) / 4, _prime);
        if (y * y % _prime != square)
            throw new FormatException("The public key is not on the P-256 curve.");

        if (y.IsEven != (publicKey[0] == 0x02))
            y = _prime - y;

        var yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: true);
        var paddedY = new byte[32];
        yBytes.CopyTo(paddedY.AsSpan(32 - yBytes.Length));
        return new ECPoint { X = publicKey[1..].ToArray(), Y = paddedY };
    }

    static ECPoint? TryDecodePublicKey(ReadOnlySpan<byte> publicKey)
    {
        try
        {
            return DecodePublicKey(publicKey);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static byte[] GetScriptHash(ReadOnlySpan<byte> publicKey)
    {
        _ = DecodePublicKey(publicKey);
        // PUSHDATA1(33), compressed key, SYSCALL System.Crypto.CheckSig.
        ReadOnlySpan<byte> script = [0x0c, 0x21, .. publicKey, 0x41, 0x56, 0xe7, 0xb3, 0x27];
        var digest = new RipeMD160Digest();
        digest.BlockUpdate(SHA256.HashData(script));
        var hash = new byte[20];
        digest.DoFinal(hash);
        return hash;
    }

    static string EncodeAddress(ReadOnlySpan<byte> scriptHash)
    {
        Span<byte> payload = stackalloc byte[25];
        payload[0] = 0x35;
        scriptHash.CopyTo(payload[1..21]);
        SHA256.HashData(SHA256.HashData(payload[..21])).AsSpan(0, 4).CopyTo(payload[21..]);

        var value = new BigInteger(payload, isUnsigned: true, isBigEndian: true);
        Span<char> address = stackalloc char[35];
        var offset = address.Length;
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            address[--offset] = Base58Alphabet[(int)remainder];
        }

        return new string(address[offset..]);
    }

    static void ValidateAddress(ReadOnlySpan<char> address)
    {
        if (address.Length != 34)
            throw new FormatException("Expected a Neo N3 address with 34 Base58 characters.");

        var value = BigInteger.Zero;
        foreach (var character in address)
        {
            var digit = Base58Alphabet.IndexOf(character);
            if (digit < 0)
                throw new FormatException("The Neo N3 address contains an invalid Base58 character.");

            value = value * 58 + digit;
        }

        var payload = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (payload.Length != 25 || payload[0] != 0x35)
            throw new FormatException("The Neo N3 address has an invalid version or length.");

        var checksum = SHA256.HashData(SHA256.HashData(payload.AsSpan(0, 21)));
        if (!CryptographicOperations.FixedTimeEquals(payload.AsSpan(21), checksum.AsSpan(0, 4)))
            throw new FormatException("The Neo N3 address checksum is invalid.");
    }
}
