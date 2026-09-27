using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("GroupAccountMessageCursor")]
sealed class GroupAccountMessageCursorRecord
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }
    public long LocalSequence { get; set; }
}

[Table("Groups")]
sealed class GroupRecord
{
    [Key, MaxLength(26)]
    public required string GroupId { get; set; }
    [MaxLength(42)]
    public required string RelayId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Owner { get; set; }
    public long MemberCapacity { get; set; }
    public long MemberCount { get; set; }
    public GroupInvitePolicy InvitePolicy { get; set; }
    public GroupStatus Status { get; set; }
    public GroupMembershipState Membership { get; set; }
    public GroupRole? Role { get; set; }
    public long Sequence { get; set; } = -1;
    public long Epoch { get; set; } = -1;
    public string? ManagementHash { get; set; }
    public string? Commitment { get; set; }
    public bool LocallyClosed { get; set; }
}

[Table("GroupMembers"), PrimaryKey(nameof(GroupId), nameof(AccountId))]
sealed class GroupMemberRecord
{
    public required string GroupId { get; set; }
    public required string AccountId { get; set; }
    public GroupRole Role { get; set; }
    public required byte[] PublicKey { get; set; }
    public string? Nickname { get; set; }
    public long NicknameSequence { get; set; } = -1;
    public long JoinedAtSequence { get; set; }
}

[Table("GroupBans"), PrimaryKey(nameof(GroupId), nameof(AccountId))]
sealed class GroupBanRecord
{
    public required string GroupId { get; set; }
    public required string AccountId { get; set; }
}

[Table("GroupEpochs"), PrimaryKey(nameof(GroupId), nameof(Epoch))]
sealed class GroupEpochRecord
{
    public required string GroupId { get; set; }
    public long Epoch { get; set; }
    public string? Commitment { get; set; }
    public byte[]? MemberPublicKey { get; set; }
    public string? KeyEntryJson { get; set; }
    public byte[]? ProtectedApplicationSecret { get; set; }
    public byte[]? ProtectedClientSecret { get; set; }
}

[Table("GroupMemberKeys"), PrimaryKey(nameof(GroupId), nameof(PublicKey))]
sealed class GroupMemberKeyRecord
{
    public required string GroupId { get; set; }
    public required string PublicKey { get; set; }
    public required byte[] ProtectedPrivateKey { get; set; }
    public bool Shared { get; set; }
}

[Table("GroupEvents"), PrimaryKey(nameof(GroupId), nameof(Sequence))]
[Index(nameof(GroupId), nameof(IsMessage), nameof(Sequence))]
sealed class GroupEventRecord
{
    public required string GroupId { get; set; }
    public long Sequence { get; set; }
    public required string PayloadJson { get; set; }
    public string? CertificateJson { get; set; }
    public long Epoch { get; set; }
    public string? MessageId { get; set; }
    public string? Sender { get; set; }
    public string? SenderDeviceId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? DecryptedPayloadJson { get; set; }
    public string? Rejection { get; set; }
    public bool IsMessage { get; set; }
}

[Table("GroupOperations"), PrimaryKey(nameof(GroupId), nameof(Method))]
sealed class GroupOperationRecord
{
    public required string GroupId { get; set; }
    public required string Method { get; set; }
    public required string RequestJson { get; set; }
}

[Table("GroupRotations")]
sealed class GroupRotationRecord
{
    [Key]
    public required string GroupId { get; set; }
    public required string BaseCommitment { get; set; }
    public required string Commitment { get; set; }
    public required byte[] ProtectedSecret { get; set; }
    public byte[]? OwnerPublicKey { get; set; }
    public long? ExpiresAt { get; set; }
}
