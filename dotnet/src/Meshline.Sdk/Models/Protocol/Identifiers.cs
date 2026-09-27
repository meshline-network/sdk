using Meshline.Validation;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Derives, creates, and validates device, group, channel, message, and invitation identifiers.
/// </summary>
/// <remarks>
/// Identifier validation checks representation rules. It does not verify signatures or establish current authorization.
/// </remarks>
public static class Identifiers
{
    /// <summary>
    /// Derives the device identifier from the account, device public keys, and network context.
    /// </summary>
    /// <param name="certificate">The certificate supplying the account and device public keys.</param>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical network-bound device identifier.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public static string DeriveDeviceId(DeviceCertificate certificate, NetworkContext context)
    {
        var input = new DeviceIdentity
        {
            Account = certificate.Account,
            SigningPublicKey = certificate.SigningPublicKey,
            EncryptionPublicKey = certificate.EncryptionPublicKey
        }.GetSigningInput(context);
        return "dev_" + Base64Url.EncodeToString(SHA256.HashData(input).AsSpan(0, 16));
    }

    /// <summary>
    /// Checks the prefix, size, and canonical base64url encoding of a device identifier.
    /// </summary>
    /// <param name="deviceId">The canonical device identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public static ProtocolViolation? ValidateDeviceId(string deviceId)
    {
        var value = deviceId.AsSpan();
        if (value.Length != 26 || !value.StartsWith("dev_", StringComparison.Ordinal))
            return new(ProtocolViolationKind.Format, "Expected a dev_ identifier containing 16 base64url-encoded bytes.");

        return Base64UrlValidator.IsValid(value[4..])
            ? null
            : new(ProtocolViolationKind.Format, "Expected a canonical base64url identifier.");
    }

    /// <summary>
    /// Derives a group identifier from its creator, hosting relay, creation nonce, and network context.
    /// </summary>
    /// <param name="creator">The creator's CAIP-10 account identifier.</param>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="nonce">The 16-byte resource creation nonce.</param>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical network-bound group identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="nonce"/> does not contain exactly 16 bytes.</exception>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public static string DeriveGroupId(string creator, string relayId, ReadOnlySpan<byte> nonce, NetworkContext context)
    {
        if (nonce.Length != 16)
            throw new ArgumentException("The group nonce must contain 16 bytes.", nameof(nonce));

        var input = new GroupIdentity
        {
            Creator = creator,
            RelayId = relayId,
            Nonce = [.. nonce]
        }.GetSigningInput(context);
        return "grp_" + Base64Url.EncodeToString(SHA256.HashData(input).AsSpan(0, 16));
    }

    /// <summary>
    /// Checks the prefix, size, and canonical base64url encoding of a group identifier.
    /// </summary>
    /// <param name="groupId">The canonical group identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public static ProtocolViolation? ValidateGroupId(string groupId)
    {
        var value = groupId.AsSpan();
        if (value.Length != 26 || !value.StartsWith("grp_", StringComparison.Ordinal))
            return new(ProtocolViolationKind.Format, "Expected a grp_ identifier containing 16 base64url-encoded bytes.");

        return Base64UrlValidator.IsValid(value[4..])
            ? null
            : new(ProtocolViolationKind.Format, "Expected a canonical base64url identifier.");
    }

    /// <summary>
    /// Derives a channel identifier from its creator, hosting relay, creation nonce, and network context.
    /// </summary>
    /// <param name="creator">The creator's CAIP-10 account identifier.</param>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="nonce">The 16-byte resource creation nonce.</param>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical network-bound channel identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="nonce"/> does not contain exactly 16 bytes.</exception>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public static string DeriveChannelId(string creator, string relayId, ReadOnlySpan<byte> nonce, NetworkContext context)
    {
        if (nonce.Length != 16)
            throw new ArgumentException("The channel nonce must contain 16 bytes.", nameof(nonce));

        var input = new ChannelIdentity
        {
            Creator = creator,
            RelayId = relayId,
            Nonce = [.. nonce]
        }.GetSigningInput(context);
        return "chan_" + Base64Url.EncodeToString(SHA256.HashData(input).AsSpan(0, 16));
    }

    /// <summary>
    /// Checks the prefix, size, and canonical base64url encoding of a channel identifier.
    /// </summary>
    /// <param name="channelId">The canonical channel identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public static ProtocolViolation? ValidateChannelId(string channelId)
    {
        var value = channelId.AsSpan();
        if (value.Length != 27 || !value.StartsWith("chan_", StringComparison.Ordinal))
            return new(ProtocolViolationKind.Format, "Expected a chan_ identifier containing 16 base64url-encoded bytes.");

        return Base64UrlValidator.IsValid(value[5..])
            ? null
            : new(ProtocolViolationKind.Format, "Expected a canonical base64url identifier.");
    }

    /// <summary>
    /// Creates a canonical message identifier using 16 cryptographically random bytes.
    /// </summary>
    /// <returns>A new <c>msg_</c>-prefixed canonical base64url identifier.</returns>
    /// <exception cref="CryptographicException">The cryptographic random-number provider cannot generate the identifier bytes.</exception>
    public static string CreateMessageId() =>
        "msg_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Checks the prefix, size, and canonical base64url encoding of a message identifier.
    /// </summary>
    /// <param name="messageId">The canonical message identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public static ProtocolViolation? ValidateMessageId(string messageId)
    {
        var value = messageId.AsSpan();
        if (value.Length != 26 || !value.StartsWith("msg_", StringComparison.Ordinal))
            return new(ProtocolViolationKind.Format, "Expected a msg_ identifier containing 16 base64url-encoded bytes.");

        return Base64UrlValidator.IsValid(value[4..])
            ? null
            : new(ProtocolViolationKind.Format, "Expected a canonical base64url identifier.");
    }

    /// <summary>
    /// Creates a canonical group invitation identifier using 16 cryptographically random bytes.
    /// </summary>
    /// <returns>A new <c>inv_</c>-prefixed canonical base64url identifier.</returns>
    /// <exception cref="CryptographicException">The cryptographic random-number provider cannot generate the identifier bytes.</exception>
    public static string CreateInviteId() =>
        "inv_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Checks the prefix, size, and canonical base64url encoding of a group invitation identifier.
    /// </summary>
    /// <param name="inviteId">The canonical group invitation identifier.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public static ProtocolViolation? ValidateInviteId(string inviteId)
    {
        var value = inviteId.AsSpan();
        if (value.Length != 26 || !value.StartsWith("inv_", StringComparison.Ordinal))
            return new(ProtocolViolationKind.Format, "Expected an inv_ identifier containing 16 base64url-encoded bytes.");

        return Base64UrlValidator.IsValid(value[4..])
            ? null
            : new(ProtocolViolationKind.Format, "Expected a canonical base64url identifier.");
    }
}

file sealed record DeviceIdentity() : TypedProtocolModel("meshline.device.identity")
{
    public required string Account { get; init; }
    public required ImmutableArray<byte> SigningPublicKey { get; init; }
    public required ImmutableArray<byte> EncryptionPublicKey { get; init; }
}

file sealed record GroupIdentity() : TypedProtocolModel("meshline.group.identity")
{
    public required string Creator { get; init; }
    public required string RelayId { get; init; }
    public required ImmutableArray<byte> Nonce { get; init; }
}

file sealed record ChannelIdentity() : TypedProtocolModel("meshline.channel.identity")
{
    public required string Creator { get; init; }
    public required string RelayId { get; init; }
    public required ImmutableArray<byte> Nonce { get; init; }
}
