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
| `accountManager.getRoute(accountId?)` | Resolve the current account by default, or another account's route. |
| `deviceManager.getDeviceState(target?)` | Resolve own or contact-authorized device state. |
| `deviceManager.getCertificate(deviceId)` | Read a cached device certificate. |
| `deviceManager.getAuthorizationState(deviceId)` | Distinguish unknown, unregistered, not-yet-valid, expired, and authorized devices. |

The `target` for device-state lookup may be an account ID or a supported contact
grant/invitation. Passing an account ID does not bypass the account's discovery
or contact authorization rules.

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
