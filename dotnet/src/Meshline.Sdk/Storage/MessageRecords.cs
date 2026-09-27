using Meshline.Models.Client;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("Messages")]
[Index(nameof(Sender), nameof(MessageId), IsUnique = true)]
[Index(nameof(Recipient), nameof(CreatedAt))]
[Index(nameof(IsDirect), nameof(CreatedAt))]
sealed class StoredMessageRecord
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long LocalSequence { get; set; }
    [MaxLength(256)]
    public required string Sender { get; set; }
    [MaxLength(26)]
    public required string MessageId { get; set; }
    [MaxLength(26)]
    public required string SenderDeviceId { get; set; }
    [MaxLength(256)]
    public required string Recipient { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string PayloadType { get; set; }
    public string? PayloadJson { get; set; }
    public byte[]? ProtectedPayload { get; set; }
    public bool IsDirect { get; set; }
}

[Table("MessageOutbox")]
[Index(nameof(State), nameof(NextAttemptAt))]
sealed class MessageOutboxRecord
{
    [Key, MaxLength(26)]
    public required string MessageId { get; set; }
    [MaxLength(256)]
    public required string Recipient { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsDirect { get; set; }
    public MessageSendState State { get; set; }
    [MaxLength(42)]
    public required string RelayId { get; set; }
    public required string RequestJson { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
}

[Table("AccountTimelines")]
sealed class AccountTimelineRecord
{
    [Key, MaxLength(42)]
    public required string RelayId { get; set; }
    public long Sequence { get; set; } = -1;
    public bool HasRetentionGap { get; set; }
    public DateTimeOffset? LastSynchronizedAt { get; set; }
}
