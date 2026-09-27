namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a signed group invitation, signer identifier, and use count.
/// </summary>
public sealed record GroupInviteEntry : ProtocolModel
{
    /// <summary>
    /// The signed group invitation document.
    /// </summary>
    public required GroupInvite Invite { get; init; }
    /// <summary>
    /// The identifier of the device that signed the associated payload.
    /// </summary>
    public required string SignerDeviceId { get; init; }
    /// <summary>
    /// The invitation use count reported by the relay.
    /// </summary>
    public required long Uses { get; init; }
}
