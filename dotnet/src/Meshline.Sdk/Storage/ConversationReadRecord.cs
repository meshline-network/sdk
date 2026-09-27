using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("ConversationReads")]
sealed class ConversationReadRecord
{
    [Key, MaxLength(256)]
    public required string ConversationId { get; set; }
    public long Sequence { get; set; }
}
