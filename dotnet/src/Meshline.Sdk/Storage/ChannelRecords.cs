using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("Channels")]
[Index(nameof(IsFollowed), nameof(RelayId))]
sealed class ChannelRecord
{
    [Key, MaxLength(27)]
    public required string ChannelId { get; set; }
    [MaxLength(42)]
    public required string RelayId { get; set; }
    public string? DescriptorJson { get; set; }
    public bool IsFollowed { get; set; }
    [ConcurrencyCheck]
    public long Revision { get; set; } = -1;
    public long SyncSequence { get; set; } = -1;
}

[Table("ChannelDescriptors"), PrimaryKey(nameof(ChannelId), nameof(Revision))]
sealed class ChannelDescriptorRecord
{
    [MaxLength(27)]
    public required string ChannelId { get; set; }
    public long Revision { get; set; }
    public required string DocumentJson { get; set; }
    public required string CertificateJson { get; set; }
}

[Table("ChannelPosts"), PrimaryKey(nameof(ChannelId), nameof(Sequence))]
[Index(nameof(ChannelId), nameof(MessageId), nameof(Author))]
sealed class ChannelPostRecord
{
    [MaxLength(27)]
    public required string ChannelId { get; set; }
    public long Sequence { get; set; }
    [MaxLength(26)]
    public string? MessageId { get; set; }
    [MaxLength(256)]
    public string? Author { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string? PostJson { get; set; }
    public long AppliedThrough { get; set; }
    public bool IsDeleted { get; set; }
}

[Table("ChannelOperations"), PrimaryKey(nameof(RelayId), nameof(ResourceId), nameof(Method))]
sealed class ChannelOperationRecord
{
    [MaxLength(42)]
    public required string RelayId { get; set; }
    [MaxLength(64)]
    public required string ResourceId { get; set; }
    [MaxLength(32)]
    public required string Method { get; set; }
    public required string DocumentJson { get; set; }
}
