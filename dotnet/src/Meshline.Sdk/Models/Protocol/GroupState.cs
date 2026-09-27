using Meshline.Identity;
using Meshline.Validation;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains the relay-visible metadata, membership count, and lifecycle state of a group.
/// </summary>
public sealed record GroupState : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The display name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The optional human-readable description.
    /// </summary>
    public string? Description { get; init; }
    /// <summary>
    /// Whether the group is active or closed.
    /// </summary>
    public required GroupStatus Status { get; init; }
    /// <summary>
    /// The CAIP-10 account identifier of the current group owner.
    /// </summary>
    public required string Owner { get; init; }
    /// <summary>
    /// The maximum permitted number of group members.
    /// </summary>
    public required long MemberCapacity { get; init; }
    /// <summary>
    /// The current number of group members reported by the relay.
    /// </summary>
    public required long MemberCount { get; init; }
    /// <summary>
    /// The policy controlling which members can create invitations.
    /// </summary>
    public required GroupInvitePolicy InvitePolicy { get; init; }

    /// <summary>
    /// Validates member counts, capacity, lifecycle state, invitation policy, metadata, and the owner account.
    /// </summary>
    /// <param name="context">The optional network context; these field checks do not depend on it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    /// <exception cref="NotSupportedException">The group owner's account namespace is unsupported.</exception>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (MemberCount <= 0 || MemberCapacity <= 0 || !Enum.IsDefined(Status) || !Enum.IsDefined(InvitePolicy)
            || string.IsNullOrWhiteSpace(Name) || Encoding.UTF8.GetByteCount(Name) > 256
            || Description is { Length: > 0 } description && (Encoding.UTF8.GetByteCount(description) > 4096 || string.IsNullOrWhiteSpace(description))
            || AccountAdapter.ValidateAccountId(Owner) is not null)
            return new(ProtocolViolationKind.Format, "The relay returned an invalid group preview.");

        return null;
    }
}
