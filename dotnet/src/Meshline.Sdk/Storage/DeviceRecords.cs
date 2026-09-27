using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("DatabaseBinding")]
sealed class DatabaseBinding
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }
    [MaxLength(256)]
    public required string Context { get; set; }
    [MaxLength(256)]
    public required string AccountId { get; set; }
    [MaxLength(26), ConcurrencyCheck]
    public string? DeviceId { get; set; }
}

[Table("LocalDevice")]
sealed class LocalDeviceRecord
{
    [Key, MaxLength(26)]
    public required string DeviceId { get; set; }
    public required string CertificateJson { get; set; }
    public required byte[] ProtectedSigningKey { get; set; }
    public required byte[] ProtectedEncryptionKey { get; set; }
    [ConcurrencyCheck]
    public long Version { get; set; }
}

[Table("DeviceStates")]
sealed class DeviceStateRecord
{
    [Key, MaxLength(256)]
    public required string AccountId { get; set; }
    public required string DocumentJson { get; set; }
    [ConcurrencyCheck]
    public long Revision { get; set; }
}
