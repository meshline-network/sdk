# Accounts and devices

[Guide index](../README.md) · [Recovery and migration](recovery-and-migration.md)

## Account routing and device authorization

An account identity belongs to the account signer. Its signed route identifies the current home relay. A local device has its own keys and certificate, and published account device state authorizes that device to operate. A local certificate alone does not establish published authorization.

Use `AccountManager.GetRouteAsync` to resolve the current or another account's route. `client.Route`, `client.Device`, and `client.DeviceState` expose currently known state. `AccountManager.State` reports the account state; it is separate from component lifecycle and transport connection state.

For first-time setup, use the [quick-start establishment workflow](quick-start.md). It coordinates device creation, device-state publication, and route publication. Lower-level `PublishRouteAsync` and `PublishDeviceStateAsync` exist for applications managing those operations explicitly and require an account signer.

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

`GetAuthorizationState(deviceId)` distinguishes authorization conditions for a known device. `GetCertificate(deviceId)` retrieves a known certificate. `DeviceChanged` and `DeviceStateChanged` notify applications about changes; query current state again when updating UI.

## Removing devices and preserving access

`RemoveDeviceAsync` independently reads the latest device state before publishing updated authorization; it requires the account signer. Do not treat deletion of a local database as remote device revocation. Removing a device's authorization affects future authorized operations, while local copies of already retrieved data remain under the application's storage policy.

Routine startup uses an already authorized device and need not request an account signature. Expired or unavailable authorization requires deliberate handling: renew or republish with the account signer when appropriate, or use the [account recovery workflow](recovery-and-migration.md). Transport outages alone do not justify replacing authorization.

## API reference

[AccountManager](../api/Meshline.Components.AccountManager.md) · [DeviceManager](../api/Meshline.Components.DeviceManager.md) · [DeviceAuthorizationState](../api/Meshline.Components.DeviceAuthorizationState.md) · [DeviceStatePublishOptions](../api/Meshline.Models.Client.DeviceStatePublishOptions.md) · [DeviceStatePublishResult](../api/Meshline.Models.Client.DeviceStatePublishResult.md)
