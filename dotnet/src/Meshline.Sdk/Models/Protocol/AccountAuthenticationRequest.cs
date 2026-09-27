using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Models.Protocol;

/// <summary>
/// Carries an account's response to a relay authentication challenge.
/// </summary>
public sealed record AccountAuthenticationRequest : ProtocolModel
{
    /// <summary>
    /// The relay-issued challenge nonce being signed.
    /// </summary>
    public required string Nonce { get; init; }
    /// <summary>
    /// The public key used to verify the account's signature and identifier.
    /// </summary>
    public required ImmutableArray<byte> AccountPublicKey { get; init; }
    /// <summary>
    /// The account signature over the model's account signing input.
    /// </summary>
    public required ImmutableArray<byte> AccountSignature { get; init; }

    /// <summary>
    /// Builds the canonical, network-bound bytes used to sign or verify this document.
    /// </summary>
    /// <param name="accountId">The account's CAIP-10 identifier.</param>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="origin">The relay endpoint origin bound into the authentication signature.</param>
    /// <param name="context">The network context bound into identifiers or signing input.</param>
    /// <returns>The canonical UTF-8 signing input bound to the network context, with the applicable signature fields omitted.</returns>
    /// <exception cref="JsonException">The signing payload cannot be represented as canonical protocol JSON, contains conflicting extension fields, or already contains a root $context property.</exception>
    public byte[] GetSigningInput(string accountId, string relayId, string origin, NetworkContext context) =>
        new AccountAuthentication
        {
            RelayId = relayId,
            Origin = origin,
            Account = accountId,
            AccountPublicKey = AccountPublicKey,
            Nonce = Nonce
        }.GetSigningInput(context);
}

file sealed record AccountAuthentication() : TypedProtocolModel("meshline.relay.account_auth")
{
    public required string RelayId { get; init; }
    public required string Origin { get; init; }
    public required string Account { get; init; }
    public required ImmutableArray<byte> AccountPublicKey { get; init; }
    public required string Nonce { get; init; }
}
