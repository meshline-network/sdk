# Accounts and devices

An account identifies the user. Its route identifies the home relay, while its
device state authorizes device certificates. A local database holds one device's
identity and protected secrets. Create a separate database for another device.

## Establish an account

After initialization, call `client.establishAccount({ relayId })` and then
`client.start()`. The SDK coordinates local device creation, authorization, and
route publication. Omitting `relayId` selects a verified active relay.

`certificateValiditySeconds` and `routeValiditySeconds` are positive integer
durations. Both default to 365 days; certificate validity is capped at 720 days
and route validity at 3650 days. These values are seconds, not milliseconds.

An interrupted establishment retains its original relay. Preserve the store so
the SDK can reconcile the original signed requests. If the account already has a
route but this local device is unauthorized, use [explicit recovery](recovery-and-migration.md).

## Read account and device state

| API | Use |
| --- | --- |
| `client.route`, `client.device`, `client.deviceState`, `client.profile` | Current local snapshots; may be `undefined`. |
| `accountManager.getRoute(accountId?)` | Read a verified route, discovering it when the cache has no usable entry. |
| `accountManager.refreshRoute(accountId?)` | Await discovery, bypassing freshness and failure cooldown. |
| `deviceManager.getDeviceState(target?)` | Resolve own or contact-authorized device state. |
| `deviceManager.refreshDeviceState(target?)` | Await an update, retaining the one-minute failure cooldown. |
| `deviceManager.getOwnDeviceState(relayId)` | Await current work, then query the specified relay for account administration. |
| `deviceManager.getCertificate(deviceId)` | Read a cached device certificate. |
| `deviceManager.getAuthorizationState(deviceId)` | Distinguish unknown, unregistered, not-yet-valid, expired, and authorized devices. |

The `target` for device-state lookup may be an account ID or a supported contact
grant/invitation. Passing an account ID does not bypass the account's discovery
or contact authorization rules.

### Metadata caching

Routes, device states and profiles reuse verified records in the existing account-scoped
store. An ordinary read returns usable data immediately. A successful refresh or local
publication defers ordinary refresh for **one hour**; failure or missing data defers it
for **one minute**. After that interval, the next read starts a background refresh.
There is no metadata polling or timed retry. Refresh timestamps stay in memory, so a
restart can return persisted data while its first read refreshes it in the background.

These intervals do not extend protocol validity. Route expiry, current certificate
windows, contact proof signatures and revocation still apply when data is used. Without
usable data, reads await a shared request; network/validation failures remain errors.
Failed refreshes and unsigned route discovery misses retain usable data. Equivalent
results do not repeat change events. Verified newer revisions cannot be overwritten
by an older response. Notifications with only a revision record a hint for later reads.

Each manager binds its store and caches to one network, Registry and account. Device
queries also separate relay, signer and authorization proof; private records do not seed
public contact lookups. Returned snapshots are copies. Canceling a caller ends only
its wait; stop/dispose cancel and drain the component's shared refreshes.

Use `refreshRoute`, `refreshDeviceState` or `profileManager.refreshProfile` to await
an update explicitly. Route administration bypasses the failure cooldown; device and
profile refresh retain it. Establishment, recovery, migration and device removal keep
their required fresh lookups. Ordinary messaging shares the managers' caches and waits
for updates only when its authorization or explicitly selected devices require them.

## Manage devices

The device manager provides `createDevice`, `renewDevice`, `publishDeviceState`,
and `removeDevice`. Prefer the client's establishment and recovery workflows for
normal setup; they coordinate route and device publication together.

Creating or renewing a local certificate and publishing accepted authorization
are distinct steps. Do not treat a staged certificate as relay-authorized.
`removeDevice(deviceId)` coordinates removal with contact authorization updates.
Subscribe to `deviceChanged` and `deviceStateChanged` to refresh the UI.

If loading an existing identity fails, surface the error. Replacing its database
or protection key can lose access to messages and pending operations.

[All guides](../README.md)
