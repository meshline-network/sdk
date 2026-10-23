using Meshline.Interactions;
using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>A machine-readable reason why account setup did not complete.</summary>
public enum AccountEstablishmentReason
{
    /// <summary>An account-authorized change requires a signer.</summary>
    AccountSignerRequired,
    /// <summary>The signer refused authorization.</summary>
    SignatureDeclined,
    /// <summary>The caller must select a device to replace.</summary>
    DeviceSelectionRequired,
    /// <summary>Local secret access must be restored or diagnosed.</summary>
    LocalKeyAccessRequired,
    /// <summary>A state read, signature request or definitively rejected publication prevented setup.</summary>
    AccountStateUnavailable,
    /// <summary>The supplied relay differs from the existing account or unfinished operation.</summary>
    RelayMismatch,
    /// <summary>Removing the selected device could break contact authorization.</summary>
    ContactContinuityUnverifiable,
    /// <summary>A submission may have reached the relay; retry to reconcile it.</summary>
    SubmissionOutcomeUnknown,
    /// <summary>The route or local-device authorization is not yet confirmed or is no longer effective.</summary>
    AuthorizationNotYetEffective,
    /// <summary>A revision conflict or insufficient original-route evidence prevented safe publication.</summary>
    StateConflict,
    /// <summary>The supplied options conflict with an unfinished operation.</summary>
    OperationParametersConflict
}

/// <summary>A verified device certificate and its canonical identifier for caller selection.</summary>
public sealed class AccountDeviceInfo
{
    /// <summary>The exact identifier to pass as <see cref="AccountEstablishmentOptions.ReplaceDeviceId"/>.</summary>
    public required string DeviceId { get; init; }
    /// <summary>The signed certificate, including public keys and validity timestamps.</summary>
    public required DeviceCertificate Certificate { get; init; }
}

/// <summary>The current complete device list supplied when explicit selection is needed.</summary>
public sealed class DeviceSelectionInfo
{
    /// <summary>The protocol's maximum number of certificates, including expired certificates.</summary>
    public int DeviceLimit { get; init; } = 8;
    /// <summary>The revision of the verified device-state snapshot.</summary>
    public required long Revision { get; init; }
    /// <summary>The previously stored local device identifier, if known.</summary>
    public string? PreviousLocalDeviceId { get; init; }
    /// <summary>The complete snapshot; entries are not ranked or automatically selected.</summary>
    public required IReadOnlyList<AccountDeviceInfo> Devices { get; init; }
}

/// <summary>A contact authorization that cannot safely survive the requested replacement.</summary>
public sealed class ContactAuthorizationIssue
{
    /// <summary>The affected contact account, or null when the contact history itself is unavailable.</summary>
    public string? AccountId { get; init; }
    /// <summary>A stable diagnostic code describing the missing continuity evidence.</summary>
    public required string Code { get; init; }
}

/// <summary>Structured failure information from explicit account establishment.</summary>
/// <param name="reason">The machine-readable failure reason.</param>
/// <param name="message">A diagnostic description, not an instruction to display a particular interface.</param>
/// <param name="innerException">The original failure, when available.</param>
public sealed class AccountEstablishmentException(AccountEstablishmentReason reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The machine-readable failure reason.</summary>
    public AccountEstablishmentReason Reason { get; } = reason;
    /// <summary>The complete verified device snapshot for selection or continuity failures, when available.</summary>
    public DeviceSelectionInfo? DeviceSelection { get; internal set; }
    /// <summary>The local key-access diagnosis, when available.</summary>
    public SecretAccessFailure? KeyIssue { get; internal set; }
    /// <summary>Contact continuity problems that prevented the requested removal.</summary>
    public IReadOnlyList<ContactAuthorizationIssue> ContactIssues { get; internal set; } = [];
}
