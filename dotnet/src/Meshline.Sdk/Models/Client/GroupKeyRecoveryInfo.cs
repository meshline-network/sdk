using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains a member key recovery request and its acceptance and expiry information.
/// </summary>
public sealed class GroupKeyRecoveryInfo
{
    /// <summary>
    /// The group and hosting relay associated with this information.
    /// </summary>
    public required GroupRef Group { get; init; }
    /// <summary>
    /// The signed group member key recovery request.
    /// </summary>
    public required GroupMemberRecoveryRequest Request { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The relay acceptance time.
    /// </summary>
    public required DateTimeOffset AcceptedAt { get; init; }
    /// <summary>
    /// The expiration time.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
