# Accounts and devices

[Guide index](../README.md) · [Recovery and migration](recovery-and-migration.md)

## Account routing and device authorization

An account identity belongs to the account signer. Its signed route identifies the current home relay. A local device has its own keys and certificate, and published account device state authorizes that device to operate. A local certificate alone does not establish published authorization.

Use `AccountManager.GetRouteAsync` to resolve the current or another account's route. `client.Route`, `client.Device`, and `client.DeviceState` expose currently known state. `AccountManager.State` reports the account state; it is separate from component lifecycle and transport connection state.

For explicit setup, including new devices and expired authorization, use [idempotent account establishment](account-establishment.md). It coordinates durable keys, device-state publication, route renewal, and initial profile creation. Lower-level `PublishRouteAsync` and `PublishDeviceStateAsync` exist for applications managing those operations explicitly and require an account signer.

On the same client, callers must await establishment before directly creating,
renewing, removing, or publishing devices, and await any ongoing device mutation
before starting establishment. This ordering also applies to signing/event callbacks;
completion, failure, or cancellation must finish before the other operation starts.

For route management alone, use `AccountManager.EnsureRouteAsync(relayId, validity,
cancellationToken)`. It retains an unexpired verified route without network requests
or account signing, reconciles an unfinished publication even when an older valid
route exists, and renews only when necessary. It reads remote state before any
publication. `validity` applies to a newly created route; it does not require an
existing route to have that much time remaining. An unfinished request must use its
original relay and lifetime. `PublishRouteAsync` remains the explicit publication
API, including revision selection and recovery behavior.

First route publication requires current authoritative or unexpired staged device
state at that relay. Complete setup should use `EstablishAccountAsync`; successful
route assurance alone does not confirm local-device authorization or initialize
the profile. It does not migrate relays or start synchronization. Keep one manager
per local account session, and finish explicit home-relay migration through the
client's migration API before running account establishment.

Catch `AccountRouteException` and use `Failure` to decide what must change before
another explicit call to `EnsureRouteAsync`:

| Failure | Handling |
| --- | --- |
| `StateUnavailable` | Restore access to complete, verified route state before retrying. A failed query does not prove account absence. |
| `SubmissionUnknown` | Retry with the original relay and lifetime to reconcile the retained signed request; the submission may already have succeeded. |
| `StateConflict` | Obtain newer authoritative state or correct relay prerequisites when the relay rejects a revision and discovery cannot resolve the conflict. |
| `SignerRequired` | Supply an account signer before retrying. |
| `SignatureDeclined` | Let the caller obtain renewed user consent before retrying account signing. |
| `RelayMismatch` | Use the retained or unfinished publication's relay; resolve intentional migration through the migration API. |
| `OperationConflict` | Resume the unfinished publication with its original lifetime. |

A later explicit call may retry a definitively rejected unexpired route request
after fresh discovery. Superseding an expired request requires retained verified
original-route evidence; its saved revision alone is insufficient. See
[protocol limits](account-establishment.md#protocol-limits-and-concurrency) before
recovering an installation with expired or undiscoverable route history.
Cancellation preserves the request and does not undo remote acceptance.

### Metadata reads and refreshes

A normal read returns usable cached metadata immediately. Reads trigger background refreshes **one hour after success**, or **one minute after failure or a missing result**, measured from completion. There are no refresh timers. Compatible concurrent requests share a refresh; canceling one caller's wait does not cancel it.

Refresh intervals do not determine validity. Operations still check signed expiry, authorization, revocation and required versions. Missing or unusable data requires an awaited fetch, with the same one-minute failure cooldown. Errors propagate from necessary fetches; background failures preserve usable data. A route, device-state or profile `not_found` response does not revoke an existing valid object.

| Data and owner | Key and storage | Use and invalidation |
| --- | --- | --- |
| Registry directory and entries, optional `CachedRelayRegistry` | Source + network + Registry; directory or relay ID; memory | Validated removal or inactive status prevents relay use. See [provider selection and ownership](integrations.md). |
| Relay descriptors and limits, `RelayClientPool` | Pool + relay ID; memory, shared across sessions | Signed descriptor expiry and current capability requirements apply. `GetInfoAsync` remains live because it includes server time. |
| Account routes, `AccountManager` | Target account; memory and `AccountRoutes` | Verified before storage; reload checks binding, revision and expiry without repeating signatures. |
| Device states and certificates, `DeviceManager` | Target account + relay + signing identity + access proof; `DeviceStates` | State has no TTL; certificate validity and proof authorization still apply. Private queries cannot authorize public discovery. New revisions and revocations apply across query scopes. |
| Profiles, `ProfileManager` | Target account; memory and `AccountProfiles` with signer evidence | No TTL; timestamp and publication ordering prevent stale replacement. |
| Channel descriptors, `ChannelManager` | Channel ID + relay; memory and `Channels`/`ChannelDescriptors` | Revision, identity and closure checks apply; timeline history uses the exact descriptor revision. |
| Contacts and grants, `MessageManager` | Contact account; `Contacts` | Current relationship, grant expiry and signatures are checked when used. |

Components, pools and databases are bound to their network, Registry and local account; use a separate pool and database when switching accounts. Valid stored metadata is reused after restart. Refresh timing stays in memory, so the first read can return stored data and trigger a refresh. Reads and failed refreshes never extend protocol validity.

`RefreshRouteAsync`, `RefreshDeviceStateAsync`, `RefreshProfileAsync` and `RefreshChannelAsync` await refreshes while ordinary cached reads remain available. Only `RefreshRouteAsync` also bypasses the failure cooldown. Administrative operations retain their latest-state checks. Accepted publications update the same caches; older responses cannot replace them, and unchanged results do not repeat change events.

Device revision notifications alone do not invalidate usable state. A refresh below the announced revision follows the failure cooldown. Reception refreshes state when cached authorization is insufficient; failed lookups preserve the processing cursor, while verified denial rejects the message.

This policy does not change message/timeline synchronization, group management or connection maintenance. Stop and disposal drain component refreshes; pool and Registry wrapper lifetimes are [application-owned](transport-and-di.md).

## Renew a local device

Prerequisites: initialize the client with the account signer, resolve its route, and retain the local device keys. Select a valid certificate lifetime for your application. Renew the certificate and publish the resulting device state:

<!-- snippet: device-renewal -->
```csharp
public static async Task RenewDeviceAsync(
    MeshlineClient client,
    TimeSpan validity,
    CancellationToken cancellationToken = default)
{
    var route = client.Route ?? throw new InvalidOperationException("Resolve the account route first.");
    await client.DeviceManager.RenewDeviceAsync(validity, cancellationToken);
    var publication = await client.DeviceManager.PublishDeviceStateAsync(
        route.RelayId, cancellationToken: cancellationToken);
    Console.WriteLine($"Device-state publication: {publication.Status}");
}
```
<!-- /snippet -->

Source: [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs). Inspect `DeviceStatePublishResult.Status`: a relay can temporarily stage a state instead of accepting it as authoritative. `StagedUntil` describes that temporary state when available. Do not present a staged publication as completed authorization.

Staged submissions and submissions with an unknown outcome remain pending across
restart. For an exact retry, pass the same complete certificate list, relay and
revision (if one was supplied); the SDK reuses the signed request. A call without a
certificate list performs a refresh and may advance the revision. Authoritative
state reads reconcile pending requests; a temporary staging response does not.

`GetAuthorizationState(deviceId)` distinguishes authorization conditions for a known device. `GetCertificate(deviceId)` retrieves a known certificate. `DeviceChanged` and `DeviceStateChanged` notify applications about changes; query current state again when updating UI.

## Declare an offline push gateway

Pass the application's complete public HTTPS callback URL through
`AccountEstablishmentOptions.PushGateway` when establishing a new local device.
The SDK preserves the exact string, including query text, without appending a
path or normalizing it.

<!-- snippet: push-gateway-options -->
```csharp
public static Task EstablishPushEnabledAccountAsync(
    MeshlineClient client, string pushGateway, CancellationToken cancellationToken = default) =>
    client.EstablishAccountAsync(new AccountEstablishmentOptions
    {
        PushGateway = pushGateway
    }, cancellationToken);
```
<!-- /snippet -->

The URL must begin with `https://`, have a valid host, and fit within 512 UTF-8 bytes.
Whitespace, userinfo, backslashes, and fragments (including an empty `#`) are
invalid. Omit the option for no declaration; an empty string is invalid. URL
validation does not contact the endpoint or establish that it is publicly reachable.
Do not include FCM/APNs tokens, callback secrets, or platform credentials.

The parameter applies only to **new devices**. An existing certificate or persisted
preparation draft retains its own declaration. Ordinary renewal, expired-certificate
recovery, and restart preserve that declaration, including its absence.

Applications creating the local certificate separately can use
`DeviceCreationOptions`. This method stores the certificate locally; call
`EstablishAccountAsync` afterwards to publish and confirm authorization.
The existing `CreateDeviceAsync(TimeSpan, ...)` overload creates without a gateway.

<!-- snippet: push-device-creation -->
```csharp
public static Task<Meshline.Models.Protocol.DeviceCertificate> CreatePushEnabledDeviceAsync(
    MeshlineClient client, string pushGateway, CancellationToken cancellationToken = default) =>
    client.DeviceManager.CreateDeviceAsync(new DeviceCreationOptions
    {
        Validity = TimeSpan.FromDays(365),
        PushGateway = pushGateway
    }, cancellationToken);
```
<!-- /snippet -->

### Change or remove a declaration

For an existing device, pass `DeviceRenewalOptions` to `RenewDeviceAsync` and publish
the resulting certificate. The original `RenewDeviceAsync(TimeSpan, ...)` overload
retains the current gateway declaration.

| Renewal options | Gateway declaration |
| --- | --- |
| Omit `PushGateway`, or use `FieldUpdate<string>.Unspecified` / `default` | Retain the current declaration. |
| Set `PushGateway` to a complete HTTPS URL | Add or replace the declaration. |
| Set `PushGateway = FieldUpdate<string>.Delete` | Remove the declaration. |

`FieldUpdate<T>` is in `Meshline.Models`. A string converts to a field assignment
implicitly. A null or empty assignment is invalid; use `Delete` to remove the
declaration. Set `Validity` for the new certificate lifetime; it defaults to 365 days.

<!-- snippet: push-gateway-update -->
```csharp
public static async Task RenewDeviceWithGatewayAsync(
    MeshlineClient client,
    FieldUpdate<string> pushGateway,
    TimeSpan validity,
    CancellationToken cancellationToken = default)
{
    // Use an initialized, stopped client constructed with an account signer.
    // Pass default to retain the gateway, a URL to set it,
    // or FieldUpdate<string>.Delete to remove the declaration.
    await client.DeviceManager.RenewDeviceAsync(new DeviceRenewalOptions
    {
        Validity = validity,
        PushGateway = pushGateway
    }, cancellationToken);
    // Publish and confirm the exact local certificate through the resumable workflow.
    await client.EstablishAccountAsync(cancellationToken: cancellationToken);
}
```
<!-- /snippet -->

Source: [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs). Removing a
declaration omits `push_gateway` from the certificate; wire-level `null` is rejected.
Both account and device signatures cover the address,
while `DeviceId`, device keys, message history, and contact relationships remain
unchanged. Existing device sessions are retired and subsequent authentication uses
the new certificate.

### Publish and resume

`RenewDeviceAsync` stores the new certificate locally before any publication.
Its return alone does not confirm relay acceptance. `EstablishAccountAsync` publishes
and verifies effective authorization; if it fails or is interrupted, resume that
workflow after restart instead of reissuing the certificate. An unresolved device
publication or preparation blocks another standalone update. Applications managing
publication themselves may use `PublishDeviceStateAsync` as described above and
must distinguish `accepted` from `staged` or unknown outcomes.

Interrupted creation retains its original gateway intent, even before a draft
exists. Omit `PushGateway` when resuming; an explicit conflicting address is rejected.
See [account establishment options](account-establishment.md#options) for the retry contract.

This SDK support declares and publishes the gateway. Relay offline detection,
gateway confirmation and callbacks, platform token registration, and FCM/APNs
delivery remain integration responsibilities. Publishing at the home relay does
not by itself prove other group relays have adopted the new declaration or that a
phone can receive a push notification.

## Removing devices and preserving access

`RemoveDeviceAsync` independently reads the latest device state before publishing updated authorization; it requires the account signer. Do not treat deletion of a local database as remote device revocation. Removing a device's authorization affects future authorized operations, while local copies of already retrieved data remain under the application's storage policy.

Standalone removal and `EstablishAccountAsync` with `ReplaceDeviceId` use the same
contact-continuity checks. A valid selected device requires retained contact-history
provenance and local issuance history. Active grants that depend on that device
must have a confirmed, valid signature from a remaining device on the same grant
content. An expired confirmation or confirmation of an older grant is insufficient.
Missing evidence blocks publication, even when the local contact list is empty.
Expired devices no longer supply usable signatures and do not create this hazard.

`RemoveDeviceAsync` checks existing evidence and reports failure with
`InvalidOperationException`; it does not deliver grant updates. Account setup also
checks existing evidence without delivering updates and returns structured
`ContactIssues` through `AccountEstablishmentException`. Both operations leave
background synchronization under the caller's control. See
[contact authorization continuity](account-establishment.md#contact-authorization-continuity) for background grant updates, retry and
history limitations.

Routine startup uses an already authorized device and need not request an account signature. Expired or unavailable authorization requires deliberate handling: renew or republish with the account signer when appropriate, or use the [account recovery workflow](recovery-and-migration.md). Transport outages alone do not justify replacing authorization.

## API reference

[AccountManager](../api/Meshline.Components.AccountManager.md) · [DeviceManager](../api/Meshline.Components.DeviceManager.md) · [DeviceAuthorizationState](../api/Meshline.Components.DeviceAuthorizationState.md) · [DeviceStatePublishOptions](../api/Meshline.Models.Client.DeviceStatePublishOptions.md) · [DeviceStatePublishResult](../api/Meshline.Models.Client.DeviceStatePublishResult.md)
