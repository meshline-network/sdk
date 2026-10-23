using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Meshline.Storage;

enum AccountEstablishmentStep
{
    AuthorizingDevice,
    EnsuringRoute,
    InitializingProfile
}

[Table("AccountEstablishment")]
sealed class AccountEstablishmentRecord
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;
    [MaxLength(42)]
    public required string RelayId { get; set; }
    public Guid OperationId { get; set; }
    // The next idempotent task, saved before dispatch; it is not evidence of relay acceptance.
    public AccountEstablishmentStep Step { get; set; }
    public long CertificateValiditySeconds { get; set; } = 31536000;
    public long RouteValiditySeconds { get; set; } = 31536000;
    public string? InitialProfileJson { get; set; }
    public bool IsInitialState { get; set; }
    // Keep the original choice until the device owner resolves a proposed change.
    [MaxLength(26)]
    public string? ReplaceDeviceId { get; set; }
    [MaxLength(26)]
    public string? PendingReplaceDeviceId { get; set; }
    [MaxLength(26)]
    public string? PreviousLocalDeviceId { get; set; }
    // Freezes an accepted choice while authorization remains effective; retries still verify current state.
    public bool DeviceAuthorizationConfirmed { get; set; }
}
