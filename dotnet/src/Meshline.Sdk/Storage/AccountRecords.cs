using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

[Table("AccountRoutes")]
sealed class AccountRouteRecord
{
    [Key, MaxLength(256)]
    public required string AccountId { get; set; }
    public required string DocumentJson { get; set; }
    [ConcurrencyCheck]
    public long Revision { get; set; }
}

[Table("AccountProfiles")]
sealed class AccountProfileRecord
{
    [Key, MaxLength(256)]
    public required string AccountId { get; set; }
    public required string DocumentJson { get; set; }
    public required string SignerCertificateJson { get; set; }
    [ConcurrencyCheck]
    public long UpdatedAt { get; set; }
}

[Table("SignedRequests")]
sealed class SignedRequestRecord
{
    [Key, MaxLength(128)]
    public required string Method { get; set; }
    [MaxLength(42)]
    public required string RelayId { get; set; }
    public required string DocumentJson { get; set; }
    [ConcurrencyCheck]
    public long Revision { get; set; }
    public bool Pending { get; set; }
}
