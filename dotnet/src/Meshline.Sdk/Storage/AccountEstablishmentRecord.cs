using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("AccountEstablishment")]
sealed class AccountEstablishmentRecord
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;
    [MaxLength(42)]
    public required string RelayId { get; set; }
}
