using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("DevicePreparation")]
sealed class DevicePreparationRecord
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;
    public Guid? OperationId { get; set; }
    // Last committed activation, retained independently of the optional draft below.
    public Guid? ActivatedOperationId { get; set; }
    public string? DraftCertificateJson { get; set; }
    public byte[]? ProtectedSigningKey { get; set; }
    public byte[]? ProtectedEncryptionKey { get; set; }
}

[Table("DatabaseBinding")]
sealed class DatabaseBinding
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }
    [MaxLength(256)]
    public required string Context { get; set; }
    [MaxLength(256)]
    public required string AccountId { get; set; }
    [MaxLength(26)]
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
