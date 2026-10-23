using Meshline.Interactions;
using Meshline.Models.Client;

namespace Meshline.Components;

// Device-domain contracts: no account workflow records, progress or exceptions.
sealed record DeviceAuthorizationIntent(Guid OperationId, string RelayId, TimeSpan Validity,
    string? ReplaceDeviceId, string? ProposedReplaceDeviceId, string? PreviousLocalDeviceId);

sealed record DeviceAuthorizationInspection(bool IsAuthorized, string? PreviousLocalDeviceId);

enum DeviceAuthorizationStatus { AwaitingRoute, Authorized }
// Acceptance of a proposed choice, not confirmation of relay authorization.
enum DeviceReplacementDisposition { Unresolved, Accepted, Rejected }
enum DeviceAuthorizationFailure
{
    SignerRequired, SignatureDeclined, SelectionRequired, KeyAccessRequired, StateUnavailable,
    ContinuityUnverifiable, SubmissionUnknown, NotEffective, StateConflict, OperationConflict,
    ReplacementConflict, PublicationRejected
}

sealed class DeviceAuthorizationException(DeviceAuthorizationFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public DeviceAuthorizationFailure Failure { get; } = failure;
    public DeviceReplacementDisposition ReplacementDisposition { get; set; }
    public SecretAccessFailure? KeyIssue { get; init; }
    public DeviceSelectionInfo? DeviceSelection { get; init; }
    public IReadOnlyList<ContactAuthorizationIssue> ContactIssues { get; init; } = [];
}
