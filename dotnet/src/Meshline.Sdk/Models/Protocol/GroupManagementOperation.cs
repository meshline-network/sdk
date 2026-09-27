using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Provides the group identifier, previous management hash, and signature for a management-chain operation.
/// </summary>
public abstract record GroupManagementOperation : TypedProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The hash of the preceding management operation used to extend the group's management chain.
    /// </summary>
    public required string PrevHash { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }

    /// <summary>
    /// Initializes a new instance of <see cref="GroupManagementOperation"/>.
    /// </summary>
    /// <param name="type">The protocol discriminator to serialize as <c>$type</c>.</param>
    protected GroupManagementOperation(string type) : base(type) { }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "device_signature");

    /// <summary>
    /// Validates the group identifier, preceding management hash, and signature length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        var hash = PrevHash.AsSpan();
        if (hash.Length != 50 || !hash.StartsWith("sha256:", StringComparison.Ordinal) || !Base64UrlValidator.IsValid(hash[7..]))
            return new(ProtocolViolationKind.Format, "The previous management hash must contain 32 canonical base64url-encoded bytes after sha256:.");
        return DeviceSignature.AsSpan().Length != Ed25519.SignatureSize
            ? new(ProtocolViolationKind.Format, "The group management signature must contain 64 bytes.")
            : null;
    }

    private protected static ProtocolViolation? ValidateAccounts(ImmutableArray<string> accounts)
    {
        if (accounts.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "The account list must not be empty.");
        var unique = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var account in accounts)
            {
                if (account is null)
                    return new(ProtocolViolationKind.Format, "The account list cannot contain null.");
                if (AccountAdapter.ValidateAccountId(account) is { } accountViolation)
                    return accountViolation;
                if (!unique.Add(account))
                    return new(ProtocolViolationKind.Format, "The account list cannot contain duplicates.");
            }
            return null;
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }

    private protected static ProtocolViolation? ValidateMembers(ImmutableArray<GroupMemberKey> members)
    {
        if (members.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "The member list must not be empty.");
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member is null)
                return new(ProtocolViolationKind.Format, "The member list cannot contain null.");
            if (member.Validate() is { } memberViolation)
                return memberViolation;
            if (!accounts.Add(member.Account))
                return new(ProtocolViolationKind.Format, "The member list cannot contain duplicate accounts.");
        }
        return null;
    }
}
