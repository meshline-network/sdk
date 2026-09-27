using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>
/// Contains a group admission application and the certificate and acceptance time returned with it.
/// </summary>
public sealed class GroupApplicationInfo
{
    /// <summary>
    /// The group and hosting relay to which the application applies.
    /// </summary>
    public required GroupRef Ref { get; init; }
    /// <summary>
    /// The signed application to join the group.
    /// </summary>
    public required GroupApplication Application { get; init; }
    /// <summary>
    /// The device certificate supplied as evidence for the associated payload's signature.
    /// </summary>
    public required DeviceCertificate SignerCertificate { get; init; }
    /// <summary>
    /// The relay acceptance time.
    /// </summary>
    public required DateTimeOffset AcceptedAt { get; init; }
}
