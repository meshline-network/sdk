# Recovery and home-relay migration

Ordinary restart, account recovery, and home-relay migration are different actions.
Choose the action deliberately; do not use recovery as a generic error fallback.

| Situation | Action |
| --- | --- |
| Same local device after a normal restart | Reopen the same store, initialize, and start. |
| Existing account without an authorized local device | Supply an account signer and call `recoverAccount(options)`. |
| Move account authorization to another relay | Call `changeHomeRelay(nextRelayId)`. |
| Account works but this device lacks group secrets | Use [group key synchronization or recovery](groups.md#recover-group-access). |

## Recover an account

`recoverAccount` accepts the establishment options plus `previousDeviceState`,
`deviceStateRevision`, and `routeRevision`. Recovery may change authoritative
route and device-state revisions. Supply complete known previous device state
when available; recovery preserves it rather than intentionally dropping other
devices. Revision overrides must advance beyond known state.

The `recover` function in [workflows.ts](../../examples/workflows.ts) calls recovery
on an initialized client, then starts it. Keep user authorization and account
signer access in the application's recovery flow.

Preserve pending signed requests after a lost response. Interruption does not
prove rejection: a relay may already have accepted the request. Reopen with the
same store and protector so the SDK can retry the exact bytes or reconcile
acceptance from verified state.

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
Observe [background failures](events-and-troubleshooting.md) to distinguish
temporary transport trouble from authorization or state conflicts.

[All guides](../README.md)
