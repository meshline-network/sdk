# Accounts and devices

An account identifies the user. Its route identifies the home relay, while its
device state authorizes device certificates. A local database holds one device's
identity and protected secrets. Create a separate database for another device.

## Establish an account

After initialization, call `client.establishAccount({ relayId })` and then
`client.start()`. The SDK coordinates local device creation, authorization, and
route publication and missing-profile initialization. Existing accounts use the
same entry point. Omitting `relayId` selects a verified active relay only for a new
account; established accounts keep their original relay.

`certificateValiditySeconds` and `routeValiditySeconds` are positive integer
durations. Both default to 365 days; certificate validity is capped at 720 days
and route validity at 3650 days. These values are seconds, not milliseconds.

An interrupted establishment retains its original relay. Preserve the store so
the SDK can reconcile the original signed requests. Read the
[account establishment contract](account-establishment.md) for retained keys,
renewal, explicit replacement, structured errors and recovery limits.

## Read account and device state

| API | Use |
| --- | --- |
| `client.route`, `client.device`, `client.deviceState`, `client.profile` | Current local snapshots; may be `undefined`. |
| `accountManager.getRoute(accountId?)` | Read a verified route, discovering it when the cache has no usable entry. |
| `accountManager.refreshRoute(accountId?)` | Await discovery, bypassing freshness and failure cooldown. |
| `accountManager.ensureRoute(relayId, validitySeconds)` | Reconcile or renew the original route, preserving unresolved signed requests. |
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
`removeDevice(deviceId)` checks contact continuity without sending authorization
updates itself. Message synchronization performs those updates in the background.
Subscribe to `deviceChanged` and `deviceStateChanged` to refresh the UI.

## Declare an offline push gateway

Pass the application's complete HTTPS callback address when creating a device:

```typescript
await client.establishAccount({ pushGateway: 'https://push.example.com/api/push/notifications' });
```

`pushGateway` is a creation parameter on `AccountEstablishmentOptions`,
`AccountRecoveryOptions` and `DeviceCreationOptions`.
Existing certificates and saved drafts retain their own declaration,
including its absence, even when a different creation address is supplied.
`createDevice({ validitySeconds, pushGateway })` creates a local certificate;
follow it with `establishAccount()` to publish and confirm authorization.
The existing `createDevice(validitySeconds)` overload omits the gateway.

The address must begin with `https://`, contain a valid host, and fit within
512 UTF-8 bytes. Query text is allowed. Whitespace, userinfo, backslashes and
fragments (including an empty `#`) are forbidden. The SDK retains the exact
string; it does not normalize it, append paths, contact the endpoint, or verify
public reachability. Do not include platform tokens, callback secrets or credentials.

### Change or remove a declaration

For an existing device, use `DeviceRenewalOptions`:

```typescript
import { FieldUpdate } from '@meshline/sdk';

// Use an initialized, stopped client with an account signer.
await client.deviceManager.renewDevice({
    validitySeconds: 365 * 86400,
    pushGateway: 'https://push.example.com/api/push/notifications',
});
await client.establishAccount();

// Remove the declaration, then publish and confirm the new certificate.
await client.deviceManager.renewDevice({ pushGateway: FieldUpdate.delete });
await client.establishAccount();
```

| Renewal value | Behavior |
| --- | --- |
| Omitted, `undefined`, or `FieldUpdate.unspecified` | Retain the current declaration. |
| A complete HTTPS URL | Add or replace the declaration. |
| `FieldUpdate.delete` | Remove the declaration. |

Null and empty assignments are invalid. Removing a declaration omits
`push_gateway` from the certificate; wire-level null is rejected.

Both options overloads default `validitySeconds` to 365 days and accept integer
seconds from 1 through 720 days. The original `renewDevice(validitySeconds)`
overload retains the gateway. Options are copied before asynchronous work.
The certificate's account and device signatures cover the gateway; device ID,
keys and existing history remain unchanged. Renewal retires old device sessions
so subsequent authentication uses the new certificate.

### Publish and resume

Renewal stores the certificate before publication. Its return alone does not
confirm relay acceptance. If establishment fails after renewal, resume it using
the same store instead of reissuing the certificate. Pending device preparation
or publication blocks another standalone renewal.

Interrupted creation retains its original gateway intent, even before a draft
exists. Omit `pushGateway` when resuming; an explicit conflicting address is rejected.
See [account establishment options](account-establishment.md#options-and-errors) for the retry contract.

See the compiled examples in [workflows.ts](../../examples/workflows.ts).
Relay offline detection, callbacks, gateway confirmation, platform registration
and phone delivery remain integration responsibilities. Home-relay publication
does not by itself confirm that every group relay has adopted the new declaration.

If loading an existing identity fails, surface the error. Replacing its database
or protection key can lose access to messages and pending operations.

[All guides](../README.md)
