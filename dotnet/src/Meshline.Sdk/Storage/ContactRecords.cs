using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("Contacts")]
[Index(nameof(State), nameof(UpdatedAt))]
sealed class ContactStateRecord
{
    [Key, MaxLength(256)]
    public required string AccountId { get; set; }
    public string? Alias { get; set; }
    public ContactRelationshipState State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? GrantFromJson { get; set; }
    public string? GrantToJson { get; set; }
    public string? ConfirmedGrantToJson { get; set; }
    public long? RequiredDeviceRevision { get; set; }
}

[Table("ContactRequests"), PrimaryKey(nameof(AccountId), nameof(Direction))]
sealed class ContactRequestRecord
{
    [MaxLength(256)]
    public required string AccountId { get; set; }
    public ContactRequestDirection Direction { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    [MaxLength(26)]
    public string? MessageId { get; set; }
    public required string ConsentJson { get; set; }
    public MessageSendState? SendState { get; set; }
}
