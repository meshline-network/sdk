# Recovery and home-relay migration

Choose the operation for the state you need to restore or change. Reopening local
data, authorizing a device and moving its home relay are separate actions.

| Situation | Action |
| --- | --- |
| Same local device after a normal restart | Reopen the same store, initialize, and start. |
| Existing account without an authorized local device | Supply an account signer and call `establishAccount(options)`. |
| Expired retained certificate or route | Use `establishAccount()` to renew only the expired prerequisite. |
| Move account authorization to another relay | Call `changeHomeRelay(nextRelayId)`. |
| Account works but this device lacks group secrets | Use [group key synchronization or recovery](groups.md#recover-group-access). |
| Restore contacts, held group keys and selected chat history | Initialize for the same account/network and merge a portable backup with `importBackup`; held secrets require a local device. |

## Recover an account

`recoverAccount` remains available for deliberate administrative recovery. It
accepts `relayId`, the two validity durations, creation-only `pushGateway`, `previousDeviceState`,
`deviceStateRevision`, and `routeRevision`. Recovery may change authoritative
route and device-state revisions. Supply complete known previous device state
when available; recovery preserves it rather than intentionally dropping other
devices. Revision overrides must advance beyond known state.

`pushGateway` is used only if recovery creates a new local device. A retained
device keeps its own declaration, including during expired-certificate renewal.
Use [device renewal options](accounts-and-devices.md#declare-an-offline-push-gateway)
to add, change or remove an existing declaration. Home-relay migration also retains
the declaration when renewing an expired certificate.

Use [unified establishment](account-establishment.md) for normal first use, new
installations, renewal and explicit device reauthorization. Authorization alone
does not restore old chat records; import a portable backup for retained local data.

The `recover` function in [workflows.ts](../../examples/workflows.ts) calls recovery
on an initialized client, then starts it. Keep user authorization and account
signer access in the application's recovery flow.
Complete establishment, recovery or migration before issuing standalone state updates.

Preserve pending signed requests after a lost response. Interruption does not
prove rejection: a relay may already have accepted the request. Reopen with the
same store and protector so the SDK can retry the exact bytes or reconcile
acceptance from verified state.

## Restore local data

Use `exportBackup(destination, password, options?, signal?)` and
`importBackup(source, password, signal?)` for the portable
[encrypted v1 format](../../../docs/backup-format-v1.md) shared with C#.
The destination implements `BackupSink.write(chunk, signal?)`; writes are awaited
and the destination remains open. The source is an `AsyncIterable<Uint8Array>`.
Neither operation requires seeking or network access. Passwords use Unicode NFC;
spaces and case are preserved.

Contacts and their grants, relevant signed device states, group references and
management history, nicknames, held group secrets, and channel follow choices are
always included. `directHistory` and `groupHistory` both default to
`BackupSelection.none`; choose `.all` or `.only(ids)` independently. Direct IDs are
peer accounts and group IDs identify groups. Empty subsets select no history.
Identity/private device keys, pending requests and sends, read positions, profiles,
channel posts, and attachment bytes are excluded.

```ts
export async function saveLocalData(client: MeshlineClient, destination: BackupSink, password: string, peers: readonly string[]) {
    return client.exportBackup(destination, password, {
        directHistory: BackupSelection.only(peers),
        groupHistory: BackupSelection.none,
    });
}
```

Source: [workflows.ts](../../examples/workflows.ts). Initialize the target for the
same account/network; restoring group secrets also requires an independently
created local device. Import merges into empty or populated stores, preserves
existing history, and rebuilds group membership from verified events. Imported
messages use the target's unchanged read positions to determine unread status.
Existing events report committed changes; reopen query readers to see them.
Restored active contacts participate in normal maintenance when the manager runs.

`BackupReport` counts exported records as `applied`; import reports `applied`,
`unchanged` and `conflicts`, retaining local values on conflict. Each imported record
commits independently. Failure or cancellation preserves earlier commits; retry
from the beginning without duplicating records. Success validates the complete
file, including final authentication and record count.

Export uses a consistent snapshot per component and checks dependencies before
completion. Concurrent changes can require a retry. Discard partial output after
any export failure and use a fresh destination.

Device, message, group and channel managers also expose `exportBackup` and
`importBackup` for plaintext `{type, data}` record streams. Independent callers must
ensure the records belong to the component's network/account. Use the client methods
for password protection and file binding validation.

## Change the home relay

`await client.changeHomeRelay(nextRelayId)` moves authorization and profile state.
It does not copy relay message history. The SDK stages device authorization,
publishes the new route, and restores the profile using a persisted migration
record containing its source, target, and snapshots.

On restart, `start()` resumes a pending migration before starting managers that
depend on the device. A lost profile acknowledgement reuses the original request.
An incompatible target or a route moved to a third relay remains a visible
conflict. Resolve that state instead of deleting pending records or repeatedly
switching targets.

Storage or protection failures leave uncommitted state available for retry.
Observe [background failures](events-and-errors.md) to distinguish
temporary transport trouble from authorization or state conflicts.

[All guides](../README.md)
