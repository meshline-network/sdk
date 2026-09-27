namespace Meshline.Models.Client;

/// <summary>
/// Contains a group invitation and its observed use count.
/// </summary>
public sealed class GroupInviteInfo
{
    /// <summary>
    /// The signed group invitation and its hosting relay reference.
    /// </summary>
    public required GroupInvite Invite { get; init; }
    /// <summary>
    /// The invitation use count reported by the relay.
    /// </summary>
    public required long Uses { get; init; }
}
