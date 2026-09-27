using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("HomeRelayMigration")]
sealed class HomeRelayMigrationRecord
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;
    [MaxLength(42)]
    public required string SourceRelayId { get; set; }
    [MaxLength(42)]
    public required string TargetRelayId { get; set; }
    public required string DeviceStateJson { get; set; }
    public string? ProfileJson { get; set; }
    public long RouteValiditySeconds { get; set; }
}
