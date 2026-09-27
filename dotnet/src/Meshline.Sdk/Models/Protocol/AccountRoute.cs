using Meshline.Identity;
using Meshline.Validation;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Binds an account to its home relay at a signed, expiring revision.
/// </summary>
public sealed record AccountRoute() : TypedProtocolModel("meshline.account.route")
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
    /// <summary>
    /// The public key used to verify the account's signature and identifier.
    /// </summary>
    public required ImmutableArray<byte> AccountPublicKey { get; init; }
    /// <summary>
    /// The monotonically increasing revision of the document.
    /// </summary>
    public required long Revision { get; init; }
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId { get; init; }
    /// <summary>
    /// The last update time, in Unix seconds.
    /// </summary>
    public required long UpdatedAt { get; init; }
    /// <summary>
    /// The expiration time, in Unix seconds.
    /// </summary>
    public required long ExpiresAt { get; init; }
    /// <summary>
    /// The account signature over the model's account signing input.
    /// </summary>
    public required ImmutableArray<byte> AccountSignature { get; init; }
    /// <summary>
    /// The relay signature over the model's relay signing input.
    /// </summary>
    public ImmutableArray<byte>? RelaySignature { get; init; }

    /// <summary>
    /// Selects a nonnegative safe-integer revision strictly greater than the known revision.
    /// </summary>
    /// <param name="requested">The explicitly requested revision, or <see langword="null"/> to increment the known revision.</param>
    /// <param name="known">The highest known revision; use <c>-1</c> when no revision is known.</param>
    /// <returns>The requested revision, or the known revision plus one when no revision was supplied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The selected revision is negative, exceeds 2^53 - 1, or is not greater than the known revision.</exception>
    public static long GetNextRevision(long? requested, long known)
    {
        const long maximum = 9_007_199_254_740_991;
        var next = requested ?? known + 1;
        if (next < 0 || next > maximum || next <= known)
            throw new ArgumentOutOfRangeException(nameof(requested), "A new revision must be a nonnegative safe integer greater than every known revision.");
        return next;
    }

    /// <summary>
    /// Validates route identity, revision, validity window, size, and the account signature.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// The account signature is verified. A present relay signature is checked for length but must be verified separately against the hosting relay identity.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="CryptographicException">The cryptographic provider cannot perform account or relay signature verification; ordinary invalid signatures are returned as protocol violations.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Revision < 0)
            return new(ProtocolViolationKind.Format, "The route revision must be nonnegative.");
        if (AccountPublicKey.IsDefaultOrEmpty || AccountSignature.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "The account public key and signature must not be empty.");
        if (RelaySignature is { } relaySignature && relaySignature.AsSpan().Length != 64)
            return new(ProtocolViolationKind.Format, "The relay signature must contain 64 bytes when present.");
        if (UpdatedAt < 0)
            return new(ProtocolViolationKind.Time, "The route update time must be nonnegative.");
        if (ExpiresAt <= UpdatedAt || (Int128)ExpiresAt - UpdatedAt > 315_360_000)
            return new(ProtocolViolationKind.Time, "The route validity period must be positive and cannot exceed 3650 days.");
        if (ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            return new(ProtocolViolationKind.Time, "The route has expired.");

        try
        {
            if (AccountAdapter.ValidateAccountId(Account) is { } accountViolation)
                return accountViolation;
            if (!AccountAdapter.MatchesPublicKey(Account, AccountPublicKey.AsSpan()))
                return new(ProtocolViolationKind.Identity, "The account public key does not identify the route account.");
            if (RelayIdentity.ValidateRelayId(RelayId) is { } relayViolation)
                return relayViolation;
            if (Encoding.UTF8.GetByteCount(ToJson()) > 4096)
                return new(ProtocolViolationKind.Format, "The route cannot exceed 4096 canonical JSON bytes.");
            if (!AccountAdapter.For(Account).VerifySignature(AccountPublicKey.AsSpan(), GetAccountSigningInput(context), AccountSignature.AsSpan()))
                return new(ProtocolViolationKind.Signature, "The route account signature is invalid.");

            return null;
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (FormatException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }

    /// <summary>
    /// Compares same-account routes by revision and, at equal revisions, by content excluding signatures.
    /// </summary>
    /// <param name="other">Another route belonging to the same account.</param>
    /// <returns>The revision ordering, unsigned-content equivalence, or equal-revision conflict.</returns>
    /// <remarks>
    /// Neither route is validated by this comparison. Equal revisions with different unsigned content are reported as a conflict rather than resolved by signature bytes.
    /// </remarks>
    /// <exception cref="ArgumentException">The routes belong to different accounts.</exception>
    /// <exception cref="JsonException">Either route cannot be serialized for the equal-revision unsigned-content comparison.</exception>
    public RouteComparison CompareWith(AccountRoute other)
    {
        if (Account != other.Account)
            throw new ArgumentException("Routes must belong to the same account.", nameof(other));
        if (Revision < other.Revision)
            return RouteComparison.Older;
        if (Revision > other.Revision)
            return RouteComparison.Newer;

        using var document = SerializeToDocument("account_signature", "relay_signature");
        using var otherDocument = other.SerializeToDocument("account_signature", "relay_signature");
        return JsonElement.DeepEquals(document.RootElement, otherDocument.RootElement)
            ? RouteComparison.Equivalent
            : RouteComparison.Conflict;
    }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetAccountSigningInput(NetworkContext context) =>
        GetSigningInput(context, "account_signature", "relay_signature");

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetRelaySigningInput(NetworkContext context) =>
        GetSigningInput(context, "relay_signature");
}
