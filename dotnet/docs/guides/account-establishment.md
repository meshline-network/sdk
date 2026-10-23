# Idempotent account establishment

[Guide index](../README.md) · [Accounts and devices](accounts-and-devices.md)

## Setup and runtime are separate

Call `InitializeAsync` before
`MeshlineClient.EstablishAccountAsync(AccountEstablishmentOptions? options = null,
CancellationToken cancellationToken = default)`. Successful completion confirms
the home route and local-device authorization and completes necessary profile
initialization. Call `StartAsync` separately to start synchronization. The SDK
does not supply interface controls or wallet prompts; the application supplies
an `IAccountSigner` and handles explicit user choices.

| Situation | Caller flow and setup behavior |
| --- | --- |
| First enablement | Initialize → establish → start. Setup persists device keys, stages device state, publishes the route, verifies authorization and creates the initial profile. |
| Working local device | Initialize → start. Explicit establishment is also idempotent: it verifies authorization without registration or account signing. |
| All local data lost or new installation | Initialize a new database → explicitly establish with account signing → start. Setup adds a device and preserves other devices, profile and the original relay. |
| Complete local backup restored | Initialize → establish if authorization needs checking or renewal → start. Valid original keys and authorization are reused. |
| Certificate expired | Establish with account signing. Renew only the certificate with the same keys and device slot. |
| Route expired | Establish with account signing. Renew the retained original route before querying device state. |
| Local device removed | Runtime stops. Explicit establishment with account signing can reauthorize the same device; background work never adds it back. |
| Interrupted setup | Call establish again with the same local data. Verify completed effects and reuse keys and pending signed requests. |
| State query or network failure | Retain state and diagnose/retry the failed operation. Failure does not mean the account is absent or revoked. |

The database, device keys and secret-protection material must be **all retained or
all absent**. Partial permanent loss is unsupported. Failed key access is not proof
that all data is absent; it never causes automatic clearing or key regeneration.
Readable local history remains available after initialization. **Device
reauthorization does not restore old chat history or historical decryption keys.**

Use one active client/component graph per database, including across processes,
and await disposal before transferring ownership. Stop background synchronization
before setup changes authorization. On the same client, establishment must not
overlap `CreateDeviceAsync`, `RenewDeviceAsync`, `RemoveDeviceAsync` or
`PublishDeviceStateAsync`, including from signing/event callbacks. Await completion,
failure or cancellation before starting the other operation. The SDK does not
guarantee detection of violations of this calling contract.

Ordinary startup still waits for required network authentication and can fail
offline. Show local history after initialization and handle transient startup
failures separately; see [reopening an authorized device](lifecycle.md#reopen-an-authorized-device).
Verified removal or local authentication rejection suspends runtime work without
automatic wallet signing. Observe `BackgroundError` and `StateChanged`; the
[lifecycle guide](lifecycle.md#session-order) describes shutdown and restart.

## Options

| Option | Meaning |
| --- | --- |
| `RelayId` | Optional initial target or original-relay hint. A conflicting existing route or unfinished-operation target is rejected. Use explicit migration to change relays. |
| `CertificateValidity` | Lifetime when a certificate needs issuance or renewal: 1 second–720 days, default 365 days. Valid certificates are retained. |
| `RouteValidity` | Lifetime when a route needs publication or renewal: 1 second–3650 days, default 365 days. Valid routes are retained. |
| `InitialProfile` | Creation-only `Nickname`, `Bio`, `Avatar` and `PublicDiscovery` (default false). Existing values are preserved. |
| `ReplaceDeviceId` | Exact caller-selected device to remove while authorizing the local device in one complete state publication. Omit it to resume a saved choice, or preserve all other devices when no choice is saved. |

An unfinished operation retains its saved lifetimes and profile. Omitted options
resume those settings; changing lifetimes does not restart it. A conflicting
explicit relay or initial profile is rejected. When adopting an unfinished standalone
route publication, use its original relay and route lifetime; mismatched settings
are rejected before saving a new workflow or preparing keys.

## Structured failures and device selection

`EstablishAccountAsync` returns `Task`. Catch `AccountEstablishmentException` and
inspect `Reason`; `DeviceSelection`, `KeyIssue` and `ContactIssues` provide actionable
details. `Message` and `InnerException` retain diagnostics. Retry explicitly by
calling the method again with the same database; operation identifiers and progress
are managed internally.

| Reason | Caller response |
| --- | --- |
| `AccountSignerRequired`, `SignatureDeclined` | Supply the account signer or let the user retry signing explicitly. |
| `DeviceSelectionRequired` | Present the supplied list and retry with an explicitly selected ID. |
| `LocalKeyAccessRequired` | Inspect `KeyIssue` and the original exception; restore secret access without deleting storage. |
| `AccountStateUnavailable`, `StateConflict` | Keep state, resolve the availability/conflict cause and retry. Do not infer account absence. |
| `RelayMismatch`, `OperationParametersConflict` | Resolve the conflicting options while preserving the original operation. |
| `ContactContinuityUnverifiable` | Inspect `ContactIssues`; restore continuity evidence or complete necessary grant delivery before removal. |
| `SubmissionOutcomeUnknown`, `AuthorizationNotYetEffective` | Retain local data and retry to reconcile. Staging or an unknown response is not success. |

Caller cancellation remains `OperationCanceledException` and does not undo remote
writes. Invalid arguments, storage/binding errors and unexpected implementation
failures remain exceptions. A query or signing timeout before submission is
`AccountStateUnavailable`; an attempted publication whose outcome cannot be
confirmed is `SubmissionOutcomeUnknown`. Both preserve progress.

The device limit is eight certificates, including expired ones. Renewal retains
its slot. At capacity, `DeviceSelectionRequired` supplies the complete verified
list, revision, limit and `PreviousLocalDeviceId` when known. Each entry has its
exact `DeviceId` and signed certificate with keys and validity timestamps. Compare
an entry's ID with `PreviousLocalDeviceId` to identify the previous local device;
that ID remains available through interrupted setup even if it is no longer listed.
A blank installation cannot identify its previous device. The protocol provides
no trustworthy device nickname or last-seen timestamp.

<!-- snippet: establishment-selection -->
```csharp
public static async Task<DeviceSelectionInfo?> EstablishOrGetSelectionAsync(
    MeshlineClient client, CancellationToken cancellationToken = default)
{
    try
    {
        await client.EstablishAccountAsync(cancellationToken: cancellationToken);
        return null;
    }
    catch (AccountEstablishmentException error)
        when (error.Reason == AccountEstablishmentReason.DeviceSelectionRequired)
    {
        // Return the verified list to the application. It owns the user's choice.
        return error.DeviceSelection;
    }
}

public static Task ReplaceSelectedDeviceAsync(
    MeshlineClient client, string selectedDeviceId,
    CancellationToken cancellationToken = default) =>
    client.EstablishAccountAsync(new AccountEstablishmentOptions
    {
        ReplaceDeviceId = selectedDeviceId
    }, cancellationToken);
```
<!-- /snippet -->

Source: [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs).

The application obtains the user's choice between calls. Setup rereads the full
state and validates that choice; it never substitutes another device or silently
prunes expired entries. Removing the selected device and adding the new device
use one publication. Continuity constraints below still apply.

An unresolved replacement submission freezes the choice until reconciled. Omitted
options resume it. Refusing a new proposal before its publication is saved permits
a later explicit choice. A completed, still-effective replacement retains its
choice while profile setup is unfinished; later revocation can require a new choice
against the refreshed device list.

## Keys and interrupted setup

Setup saves protected keys and the exact certificate signing input before requesting
an account signature, then saves signed requests before submission. On retry it
verifies completed effects and replays compatible unresolved requests exactly.
Progress-save failures preserve the same device. An incompatible unknown device
publication must be reconciled first, even when the current certificate still works.

The same rules apply when handing off from the standalone device APIs to setup.
Public creation and renewal reject unfinished drafts or publications. Establishment
can publish a locally committed renewal using its retained keys; confirmation
requires the current local certificate, even if its predecessor remains valid at
the relay.

Each explicit setup checks local key access before network requests. An
`ISecretProtector` can report `TemporarilyUnavailable`, `PermanentlyLost` or `Unknown`
through `SecretAccessException`. Every diagnosis preserves the database, keys, draft
and pending requests. Retry when access returns; restore a complete local backup
if retained material is permanently incomplete. Generic I/O or cryptographic
errors do not prove permanent loss.

Setup revalidates route/device authorization through profile initialization and
final cleanup. Expiry returns `AuthorizationNotYetEffective` with the original
workflow retained. Retry renews only the expired prerequisite using the same keys
and saved lifetimes, and preserves an accepted profile. Very short lifetimes can
expire again before retry completes.

A healthy account with no setup record can return despite an unrelated unfinished
route publication. Setup does not replay that request; discovery may confirm it,
or the caller can reconcile it through `EnsureRouteAsync`. A saved route step must
reconcile its request even if an older route remains valid.

## Contact authorization continuity

After `StartAsync`, `MessageManager` supplements known contact grants in the
background. Setup does not wait for this delivery. Persisted grants and outbox
records reconstruct the work if the application exits before startup. Updates
retain their message IDs and ciphertext across retries. Preparation/network failures
remain observable through `BackgroundError`; definitive rejections remain delivery
failures. Unreadable grant contents are reported and are not continuity evidence,
while other messages and updates continue processing.

Before replacement/removal, a current grant signed by the selected device needs
a confirmed valid signature from a remaining authorized device. Confirmation must
cover the same grant content; only `TargetAccepted` is delivery proof, not source
acceptance, `delivering`, or an unknown outcome. Queued/submitting/unknown outgoing
consent requests also retain their signer until resolved or canceled. Definitively
failed/canceled requests impose no delivery dependency.

If continuity evidence is missing, keep the old device, run synchronization on a
usable authorized device to complete updates, then retry. Setup and
`RemoveDeviceAsync` check evidence without sending those updates themselves.
A new installation has issuance evidence for its own newly created device, not
unrelated valid remote devices; an empty contact list does not prove their history.
Such replacement can return `ContactContinuityUnverifiable`. An expired device
supplies no currently usable signature and can be explicitly removed or replaced.

While a device publication is pending, contact authorization writes fail promptly
with `InvalidOperationException`, including after restart. Retry after reconciliation;
background maintenance uses its normal retry cycle. Account signing and relay waits
do not hold database write transactions, and ordinary message enqueue/history reads
remain available.

## Protocol limits and concurrency

Before mutation, setup reads complete verified device state from the original relay
and publishes at the observed revision plus one. A conflict causes a fresh read
and reapplication of only the local add/renew and explicit remove intent, up to
three attempts. An old list is never merged wholesale into the latest state.
Protocol 1.0 has no expected-base revision or atomic profile create-if-absent
precondition, so arbitrary concurrent writers require stronger protocol support
for strict cross-device guarantees.

Route discovery returns `not_found` for expired routes as well as absent accounts.
A retained verified original route can be renewed. A fully erased installation
whose old route has expired may need original-route evidence outside this entry
point; the protocol cannot discover all such history. Setup never responds by
changing relays or guessing a higher bootstrap revision.

Unexpired unknown route submissions reuse their original signed document. An expired
request can be superseded on the same relay only after discovery and with retained
verified original-route evidence, at a revision above both the route and saved
request. Without that evidence, an expired initial request remains `StateConflict`
with its keys and request preserved. This also limits automatic recovery of a
genuine first setup whose route response was lost and whose request has expired.
See [route management](accounts-and-devices.md#account-routing-and-device-authorization)
for direct `EnsureRouteAsync` use and its structured failures.

## API reference

[AccountEstablishmentOptions](../api/Meshline.Models.Client.AccountEstablishmentOptions.md) ·
[AccountEstablishmentException](../api/Meshline.Models.Client.AccountEstablishmentException.md) ·
[DeviceSelectionInfo](../api/Meshline.Models.Client.DeviceSelectionInfo.md) ·
[SecretAccessException](../api/Meshline.Interactions.SecretAccessException.md)
