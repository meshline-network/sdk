using Meshline.Identity;
using Meshline.Validation;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a channel's signed identity, metadata, moderation list, and lifecycle state.
/// </summary>
public sealed record ChannelDescriptor() : TypedProtocolModel("meshline.channel.descriptor")
{
    /// <summary>
    /// The channel's canonical <c>chan_</c> identifier.
    /// </summary>
    public required string ChannelId { get; init; }
    /// <summary>
    /// The 16-byte creation nonce from which the channel identifier is derived.
    /// </summary>
    public required ImmutableArray<byte> Nonce { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the resource's creator.
    /// </summary>
    public required string Creator { get; init; }
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId { get; init; }
    /// <summary>
    /// The display name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The optional human-readable description.
    /// </summary>
    public string? Description { get; init; }
    /// <summary>
    /// The account identifiers authorized to moderate the channel.
    /// </summary>
    public ImmutableArray<string>? Moderators { get; init; }
    /// <summary>
    /// The monotonically increasing revision of the document.
    /// </summary>
    public required long Revision { get; init; }
    /// <summary>
    /// Whether the channel is active or closed.
    /// </summary>
    public required ChannelStatus Status { get; init; }
    /// <summary>
    /// The creation time, in Unix seconds.
    /// </summary>
    public required long CreatedAt { get; init; }
    /// <summary>
    /// The last update time, in Unix seconds.
    /// </summary>
    public required long UpdatedAt { get; init; }
    /// <summary>
    /// The device signature over the model's device signing input.
    /// </summary>
    public required ImmutableArray<byte> DeviceSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(NetworkContext context) =>
        GetSigningInput(context, "device_signature");

    /// <summary>
    /// Validates channel metadata, member identifiers, times, status, and signature length.
    /// </summary>
    /// <param name="context">The required network context for identity and signature validation.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <remarks>
    /// These checks do not verify the external device signature or establish current signer authorization. Verify the signature and the applicable device-state or resource authorization evidence separately.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Identifiers.ValidateChannelId(ChannelId) is { } channelViolation)
            return channelViolation;
        if (Nonce.AsSpan().Length != 16)
            return new(ProtocolViolationKind.Format, "The channel nonce must contain 16 bytes.");
        if (RelayIdentity.ValidateRelayId(RelayId) is { } relayViolation)
            return relayViolation;
        if (string.IsNullOrWhiteSpace(Name) || Encoding.UTF8.GetByteCount(Name) > 256)
            return new(ProtocolViolationKind.Format, "The channel name must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.");
        if (Description is { Length: > 0 } description && (string.IsNullOrWhiteSpace(description) || Encoding.UTF8.GetByteCount(description) > 4096))
            return new(ProtocolViolationKind.Format, "A nonempty channel description must contain non-whitespace text and cannot exceed 4096 UTF-8 bytes.");
        if (Revision < 0)
            return new(ProtocolViolationKind.Format, "The channel revision must be nonnegative.");
        if (CreatedAt < 0 || UpdatedAt < 0)
            return new(ProtocolViolationKind.Time, "Channel timestamps must be nonnegative.");
        if (Revision == 0 && (Status != ChannelStatus.Active || CreatedAt != UpdatedAt))
            return new(ProtocolViolationKind.Conflict, "The initial channel descriptor must be active and have equal creation and update times.");
        if (DeviceSignature.AsSpan().Length != Ed25519.SignatureSize)
            return new(ProtocolViolationKind.Format, "The channel descriptor signature must contain 64 bytes.");

        try
        {
            if (AccountAdapter.ValidateAccountId(Creator) is { } creatorViolation)
                return creatorViolation;
            if (Moderators is { } moderators)
            {
                if (moderators.IsDefault || moderators.Length > 10)
                    return new(ProtocolViolationKind.Format, "Channel moderators must be an array containing at most 10 accounts.");
                var accounts = new HashSet<string>(StringComparer.Ordinal) { Creator };
                foreach (var moderator in moderators)
                {
                    if (AccountAdapter.ValidateAccountId(moderator) is { } moderatorViolation)
                        return moderatorViolation;
                    if (!accounts.Add(moderator))
                        return new(ProtocolViolationKind.Identity, "Channel moderators must be unique and cannot include the creator.");
                }
            }

            if (Identifiers.DeriveChannelId(Creator, RelayId, Nonce.AsSpan(), context) != ChannelId)
                return new(ProtocolViolationKind.Identity, "The channel identifier does not match its creator, relay, nonce and context.");

            return Encoding.UTF8.GetByteCount(ToJson()) <= 8192
                ? null
                : new(ProtocolViolationKind.Format, "The channel descriptor cannot exceed 8192 canonical JSON bytes.");
        }
        catch (JsonException exception)
        {
            return new(ProtocolViolationKind.Format, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}
