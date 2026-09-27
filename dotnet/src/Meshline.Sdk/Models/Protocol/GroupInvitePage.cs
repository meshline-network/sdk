using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a page of group invitations and their signer certificates.
/// </summary>
public sealed record GroupInvitePage : ProtocolModel
{
    /// <summary>
    /// The invitation entries in this page.
    /// </summary>
    public required ImmutableArray<GroupInviteEntry> Invites { get; init; }
    /// <summary>
    /// The device certificates carried by this document or page.
    /// </summary>
    public required ImmutableArray<DeviceCertificate> Certificates { get; init; }
    /// <summary>
    /// The relay's continuation cursor, or <see langword="null"/> when no next page is indicated.
    /// </summary>
    public string? Next { get; init; }
}
