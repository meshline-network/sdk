# Idempotent account establishment

`await client.establishAccount(options?, signal?)` is the explicit authorization
entry point for both first use and an existing account on a new installation.
Call `start()` separately to connect and synchronize. A normal launch with usable
retained authorization initializes and starts directly, without wallet signing or
waiting for metadata discovery. The application owns prompts and device selection.

## Local data and ownership

Keep the database, protected device keys and protection material together: local
data is either fully retained or entirely absent. Partial permanent loss is not a
supported recovery mode. Every key-access failure preserves records and pending
work. Use one active client/component graph per store, including across processes,
browser tabs and workers. Await disposal before transferring ownership.

Stop synchronization before setup needs to change authorization; already effective
authorization can be checked while running. Do not overlap setup with standalone
device creation, renewal, publication or removal, including from signer/event
callbacks. Local store compare-and-commit still protects independent message and
contact writers, and relay conflicts retain revision checks.

## Setup scenarios

| Situation | Behavior |
| --- | --- |
| First account | Save protected draft keys before wallet authorization, publish device state and route, initialize a missing profile. |
| Retained valid device and route | Confirm state and return without new keys, publications or account signatures. Ordinary startup skips this explicit setup call. |
| Existing account, empty installation | Authorize a new device at the existing relay, preserving the complete device list and profile. |
| Complete restored local data | Reuse the original keys; renew expired prerequisites or explicitly reauthorize a removed device. |
| Secret access fails | Report `localKeyAccessRequired`, preserve every record, and retry when access returns. Never delete or recreate the database. |
| Expired certificate | Reissue using the original keys and update the same list slot. |
| Expired retained route | Renew the original relay's route before reading device state; retain valid device authorization. |
| Removed local device | Stop background work. Only explicit setup with account signing reauthorizes it. |
| Interrupted setup | Reconcile completed effects, reuse draft keys and exact unresolved signed requests, and continue the saved step. |
| Network/query failure | Preserve local state, relay and device list; surface the failure instead of inferring an absent account. Local history remains readable. |

Reauthorizing a device does **not** restore lost chat history or group secrets.

## Options and errors

| Option | Purpose |
| --- | --- |
| `relayId` | Select a verified relay for an initial account. Existing accounts and unfinished operations retain their original relay. |
| `certificateValiditySeconds` | Lifetime of a new or renewed certificate: a positive integer in seconds, default 365 days, maximum 720 days. Valid certificates are reused. |
| `routeValiditySeconds` | Lifetime of a new or renewed route: a positive integer in seconds, default 365 days, maximum 3650 days. Valid confirmed routes are reused. |
| `replaceDeviceId` | Explicit device to remove in the same publication that authorizes the local device, subject to contact continuity. |
| `initialProfile` | Creation-only `nickname`, `bio`, `avatar` and `publicDiscovery`. Existing remote or retained profiles take precedence. |

Omitted options resume saved values. An incompatible lifetime/profile raises
`operationParametersConflict`. Internal operation IDs associate durable work;
callers do not supply or persist an operation ID.

The result is `Promise<void>`. Catch `AccountEstablishmentError` and inspect
`reason`, optional `deviceSelection` and `keyIssue`, and `contactIssues`:

| Reason | Application action |
| --- | --- |
| `accountSignerRequired`, `signatureDeclined` | Obtain explicit account authorization and retry when appropriate. |
| `deviceSelectionRequired` | Present the verified device list and retry with the selected exact ID. |
| `localKeyAccessRequired` | Restore access or a complete local backup; do not erase retained data. |
| `accountStateUnavailable` | Retry after the state query or relay becomes available; inspect `cause` for the failure. |
| `relayMismatch`, `operationParametersConflict`, `stateConflict` | Inspect the original parameters/state and resolve the conflict. |
| `contactContinuityUnverifiable` | Inspect `contactIssues` and complete necessary authorization delivery before removal. |
| `submissionOutcomeUnknown`, `authorizationNotYetEffective` | Keep the store and retry to reconcile; neither uncertainty nor staging is success. |

`SecretAccessError` lets a protector report `unknown`, `temporarilyUnavailable` or
`permanentlyLost`. These diagnoses never authorize database deletion. Signing
refusal can use `DOMException` with name `NotAllowedError`. Caller cancellation,
argument errors, storage/binding failures and unexpected failures retain their
original errors; inspect `cause` on structured failures for the underlying error.
`accountSignerRequired` also covers a fresh installation of an existing account
and a retained expired or revoked device. Healthy retained authorization does not
require an account signer.

## Capacity and contact continuity

The limit is eight certificates, including expired certificates. Renewal consumes
no new slot. `deviceSelection` includes the limit, verified revision, complete
certificates and `previousLocalDeviceId` when known. An empty installation cannot
identify a previous local device, and the protocol does not supply device names
or last-seen timestamps. The SDK never picks or removes a device automatically.

Pass `replaceDeviceId` to remove exactly that device and add the local device in
one state publication. An unresolved or accepted replacement retains its choice
until reconciled. A proposal rejected before publication may be changed.

Removing a valid certificate requires local issuance/history evidence and a
confirmed, valid remaining-device signature covering the same contact grant.
Pending outgoing consent also blocks removal. Only target acceptance proves
delivery; source relay acceptance and an unknown result are insufficient. An
empty local contact list does not establish history for a foreign valid device.
Expired certificates can be explicitly replaced without retaining an unusable
grant signature.

`MessageManager` supplements and delivers known contact grants after `start()`.
Setup does not wait for delivery. Persisted grants and outgoing messages allow
maintenance to resume after restart, and failures remain visible through
background errors. While a device publication is pending, new contact
authorization writes fail promptly; ordinary message enqueue and local reads
remain available.

## Confirmation and protocol limits

Before changes, setup reads the complete state from the original relay. Conflicts
reread and reapply only the intended add/renew/remove, with bounded retries.
An unresolved device request is replayed only when it matches that intent and
has a revision newer than the verified state. Rejected signed revisions remain
consumed; retrying preserves the exact saved revision. Every replay rechecks
removal continuity. Reconcile an unrelated standalone removal through its
original operation before resuming setup. An expired pending certificate is
reconciled before renewing the same keys.

Success requires accepted publication and effective authorization, not a saved
checkpoint or staged response. Profile initialization and completion recheck
validity; an expiry keeps the operation available for explicit retry.

Discovery returns `not_found` for expired routes as well as absent accounts. A
retained verified route can be renewed. After complete local erasure, an expired
remote route may require external original-route evidence; setup never switches
relays or guesses higher initial revisions. An expired unknown initial route
request without such evidence remains a conflict.

`accountManager.ensureRoute(relayId, validitySeconds, signal?)` independently
reconciles and renews an original route and reports `AccountRouteError.reason`.
It does not initialize devices/profiles or start synchronization. Unexpired
verified routes without unfinished work are reused without network or signing.
Setup itself can use a currently confirmed route even if a separate standalone
renewal is unfinished; it does not require a future renewal to finish first.

See [workflow examples](../../examples/workflows.ts), [lifecycle](lifecycle.md),
[setup storage](storage-and-pagination.md#account-setup-persistence), and
[explicit recovery and migration](recovery-and-migration.md).
