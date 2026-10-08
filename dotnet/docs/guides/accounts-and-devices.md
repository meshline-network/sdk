# Accounts and devices

[Guide index](../README.md) · [Recovery and migration](recovery-and-migration.md)

## Account routing and device authorization

An account identity belongs to the account signer. Its signed route identifies the current home relay. A local device has its own keys and certificate, and published account device state authorizes that device to operate. A local certificate alone does not establish published authorization.

Use `AccountManager.GetRouteAsync` to resolve the current or another account's route. `client.Route`, `client.Device`, and `client.DeviceState` expose currently known state. `AccountManager.State` reports the account state; it is separate from component lifecycle and transport connection state.

For first-time setup, use the [quick-start establishment workflow](quick-start.md). It coordinates device creation, device-state publication, and route publication. Lower-level `PublishRouteAsync` and `PublishDeviceStateAsync` exist for applications managing those operations explicitly and require an account signer.

### Route queries

`GetRouteAsync` reuses verified routes for **one hour** after discovery or publication. It then returns an unexpired route immediately while refreshing in the background. `ExpiresAt` remains a hard limit; without a usable route, queries await discovery. Concurrent refreshes for one account share a request; different accounts resolve independently.

Failures and `not_found` impose a **one-minute** cooldown and preserve unexpired routes. During cooldown, a query without a usable route receives the previous error or null. Background errors raise `BackgroundError`. `RefreshRouteAsync` bypasses freshness and cooldown, awaits any shared discovery, and reports its result. Establishment, recovery and migration use it. Publication immediately updates the cache and takes precedence over older responses; unchanged results do not repeat `AccountChanged`.

The cache belongs to one `AccountManager`, isolated by its network, Registry and account. Reopening requires discovery; persisted revisions still prevent rollback. Caller cancellation affects only that wait. Stop and disposal drain refreshes; stop retains verified entries and permits later queries, while disposal clears them.

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

`RemoveDeviceAsync` publishes updated device authorization; it is an account-authorized operation. Do not treat deletion of a local database as remote device revocation. Removing a device's authorization affects future authorized operations, while local copies of already retrieved data remain under the application's storage policy.

Routine startup uses an already authorized device and need not request an account signature. Expired or unavailable authorization requires deliberate handling: renew or republish with the account signer when appropriate, or use the [account recovery workflow](recovery-and-migration.md). Transport outages alone do not justify replacing authorization.

## API reference

[AccountManager](../api/Meshline.Components.AccountManager.md) · [DeviceManager](../api/Meshline.Components.DeviceManager.md) · [DeviceAuthorizationState](../api/Meshline.Components.DeviceAuthorizationState.md) · [DeviceStatePublishOptions](../api/Meshline.Models.Client.DeviceStatePublishOptions.md) · [DeviceStatePublishResult](../api/Meshline.Models.Client.DeviceStatePublishResult.md)
