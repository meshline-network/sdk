# TypeScript SDK tests

Run commands from `sdk/typescript`. Install Node.js 24 and restore the workspace first. The full suite's local HTTPS/WSS peers also need .NET 10; see [maintenance](../MAINTENANCE.md) for prerequisites and the separate browser, Expo, package and interoperability checks.

```sh
npm test
npm run typecheck
npm run test:conformance
```

`npm test` and `npm run test:conformance` build through their `pretest` hooks. For a direct filtered Vitest run, build explicitly:

```sh
npm run build
npx vitest run tests/components/history tests/components/conversations
npx vitest run tests/components/synchronization tests/runtime
npx vitest run tests/components/message-outbox.test.ts tests/components/send-status-waiting.test.ts
```

## Placement and imports

| Location under `tests/` | Responsibility |
| --- | --- |
| `protocol/`, `conformance/` | Protocol vectors and native adapters for shared behavior cases. |
| `transport/`, `integrations/`, `storage/` | Transport boundaries, external-format integrations and storage adapter contracts. |
| `runtime/` | Internal cancellation, async gates and sync-status tracking without full client workflows. |
| `components/history/` | History ordering, range bounds, snapshots, continuation and public call compatibility. |
| `components/conversations/` | Conversation summaries, observation, unread counts and explicit/default read positions. |
| `components/synchronization/` | Public sync status and foreground/background synchronization across message, group and channel managers. |
| Other `components/` tests | Account/device/profile/contact and messaging/group/channel workflows, including durable outbox and send waiting. |
| `channels/`, `groups/` | Focused internal repository, message processing and group-secret tests. |
| `support/` | Node fixtures and local peers; no test cases. |
| `portable/` | Scenarios and fixtures reused by runtime/platform acceptance. |
| `browser/`, `expo/`, `release/` | Separate browser/native acceptance harnesses and release-logic tests. |

Import public core APIs from `@meshline/sdk`, and public adapter APIs from their package names. Tests of internal modules use relative `packages/sdk/dist/...` imports. Do not mix source imports and built core modules in one test run: every case should exercise the same build. Do not export SDK internals just to make tests easier to import.

## Fixtures and case boundaries

Give each independent contract a named `test` or `test.each` case. Parameterized failures should identify the input in runner output. Keep continuous transaction rollback, stop/start and restart recovery workflows together when earlier steps are required to establish the later assertion.

Share preparation, not assertions or mutable state. Each test owns fresh resources and registers cleanup before asynchronous setup. Dispose readers in `finally` and dispose owned clients/networks after each test. Keep expectation values and failure injections visible in the test.

`MessagingNetwork`, `GroupNetwork`, `ChannelNetwork` and `ClientNetwork` remain domain fixtures. `createConversationFixture` accepts a caller-owned network and seeds incoming encrypted records through the message repository to isolate local conversation/history projections; it does not test contact negotiation or relay delivery. History-query setup stays local to its test file. These helpers are not a general scenario framework.

Use the fixture clock for SDK timers and an explicit handshake for concurrent work. Real timeouts bound a hung test; they should not determine operation order. Internal tracker tests use `MessagingClock` directly and do not construct a client network.

## Component regression coverage

| Test file | Coverage |
| --- | --- |
| `components/account-establishment.test.ts` | Suites for establishment/renewal, prerequisites, device selection/continuity, interrupted operations/conflicts, and startup/authorization loss. |
| `components/account.test.ts` | Route reconciliation, exact replay, original-relay preservation, expired publication evidence and cache/publication concurrency. |
| `components/device.test.ts` | Device-state acceptance, complete-state publication and atomic publication/grant guards. |
| `components/push-gateway.test.ts` | Suites for creation/renewal, account establishment/recovery, input validation and call isolation. |
| `protocol/push-certificate.test.ts`, `protocol/push-interop.test.ts` | Fixed push certificate/URL vectors, signature coverage, UTF-8 limits and actual C# certificate interoperability. |
| `components/message.test.ts` | Contact authorization maintenance, target acceptance and shared outbox scans with delivery-retention checks. |
| `components/device-notifications.test.ts` | Device-state notification delivery, initialization, overlapping revisions, stop/start and restart recovery. |
| `transport/pool.test.ts` | Session reuse with unchanged certificates and device-session retirement after renewal, preserving account sessions. |
| `browser/managers.spec.ts` | IndexedDB reopen after a lost acknowledgement and a fresh installation preserving devices and profile. |

The supported persistence cases are a complete retained installation or a fresh
installation. Secret-protector failures preserve retained data. Test wallets and
relays expose explicit interruption hooks; assertions verify keys, exact signed
requests, and authoritative state across reopen, not only successful completion.

Push gateway intent is an optional property of the existing JSON establishment
record. Missing properties in older records mean no creation declaration; no
adapter schema migration is needed. Browser tests verify IndexedDB reopen before
the draft exists and after an unknown gateway-update publication. Signed
certificate and URL expectations come from `push-notifications-v1.json` in the
shared snapshot; notification delivery remains a Relay/gateway responsibility.

The interoperability suite compares signed URL acceptance, exact JSON and
DeviceId with the C# driver. Its IPv6 and Unicode authority tables run under both
Node's and Expo's URL implementations on Node; native acceptance remains separate.
Component cases verify original-address retention, failure before side effects,
and exact certificate/key reuse after interruption.

Run the account lifecycle and its supporting components together after a build:

```sh
npx vitest run tests/components/account-establishment.test.ts tests/components/account.test.ts tests/components/device.test.ts tests/components/push-gateway.test.ts tests/components/message.test.ts tests/components/client.test.ts tests/transport/pool.test.ts
```

## Outgoing-message coverage

- `message-outbox.test.ts`: durable submission, retries, acceptance evidence, retention and storage contention.
- `message.test.ts`: public manager workflows and events.
- `send-status-waiting.test.ts`: immediate and future completion, milestones, cancellation, disposal and initial-query races.

The waiting test that rolls back a failed commit and then observes a completion evicted by retention deliberately crosses those boundaries. Preserve that regression alongside isolated outbox tests. The .NET and TypeScript implementations each need their own contract coverage; matching behavior is not a reason to delete one implementation's tests.

Shared, language-neutral vectors and behavior cases remain in [the shared test directory](../tests/README.md). Do not replace independent expected values with results generated by the SDK under test.
