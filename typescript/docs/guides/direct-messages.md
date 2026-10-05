# Direct messages and the outbox

Establish [contact authorization](profiles-and-contacts.md) before sending.
The client must be running to process queued sends and receive asynchronous
updates. Peers need to run to exchange contact and message data, but need not
remain simultaneously online for every message.

## Send content

The `sendText` function in [workflows.ts](../../examples/workflows.ts) queues a
message and reads its current status:

```ts
export async function sendText(client: MeshlineClient, peerAccountId: string, text: string) {
    const queued = await client.messageManager.sendMessage(peerAccountId, {
        body: { contentType: 'text/plain', text },
    });
    return client.messageManager.getSendStatus(queued.messageId);
}
```

A draft can contain a body, attachments, and a reply reference. Direct replies
use `{ from, messageId }`; attachments are content references whose hosting and
retrieval belong to your application. Omitted attachments mean no attachments.

`sendMessage()` returns a durable outbox status, not remote delivery confirmation.
Persisting the encrypted request precedes submission. Observe `sendStatusChanged`
or call `getSendStatus(messageId)` as processing continues.

## Interpret send state

| State | Meaning for the application |
| --- | --- |
| `queued` | Saved locally and eligible for cancellation before submission. |
| `submitting` | A submission is in progress. |
| `submissionUnknown` | The result is uncertain; the SDK retains the original request for reconciliation. |
| `relayAccepted` | The submitting relay acknowledged acceptance. |
| `targetAccepted` | The destination relay accepted the message; this is not a recipient read or processing receipt. |
| `failed` | Processing recorded a terminal failure; inspect `errorMessage`. |
| `canceled` | A queued request was canceled. |

Status includes message and recipient IDs, creation time, and acceptance/error
details when available. `getOutbox({ recipient, states })` opens a local snapshot:
omitting `states` includes all states; `states: []` includes none.

## Wait for an acceptance milestone

`waitForSendStatus(messageId, targetState, signal)` checks retained local status and
waits for later commits by the same manager. The target defaults to `targetAccepted`;
`queued` and `relayAccepted` are also supported. Other target values throw `RangeError`.
There is no polling, extra network request, or implicit sender startup.

```ts
export async function waitForDelivery(client: MeshlineClient, messageId: string, signal?: AbortSignal) {
    return client.messageManager.waitForSendStatus(messageId, 'targetAccepted', signal);
}
```

Source: [workflows.ts](../../examples/workflows.ts). Pass the ID returned by
`sendMessage`. The result contains the actual status: `targetAccepted` also
satisfies a `relayAccepted` wait, and `queued` accepts any recorded state.
`failed` and `canceled` end every wait, so inspect `state` before continuing as
though delivery succeeded. Target acceptance is not a read or processing receipt.

An unknown or already evicted record returns `undefined` immediately. This has the
same uncertainty as `getSendStatus`. A wait already observing a pending message
receives its committed completion even when the same transaction evicts that
terminal record. It cannot recover outcomes evicted before observation began.

Initialize the manager before waiting and keep the client or manager running for
delivery progress. Stop/start preserves waits; disposal aborts and drains them.
Changes made by another manager or process are not observed. There is no built-in
timeout: use a caller-owned `AbortController` and timer when a workflow needs a
deadline, and clear the timer when done. Aborting one wait leaves the send and
other waiters active. Query or wait on the same message ID afterward instead of
sending another message as a retry.

## Cancel or recover

`cancelMessage(messageId)` returns whether it canceled a queued message. Once
submission has begun, cancellation cannot retract remote acceptance. An
`AbortSignal` cancels the caller's operation; it does not prove the relay rejected
or rolled back a request.

For uncertain submission, keep the original database and protector. The SDK
retains the original ciphertext and accepting relay identity for retry or status
reconciliation, including across process restart. Sending a newly created message
as a generic retry can duplicate the user's message.

## Retained send history

The SDK retains the newest 1000 terminal outbox records (`targetAccepted`, `failed`,
and `canceled`) across this account, including internal protocol messages. Retention
uses `createdAt` descending, then ordinal message ID descending to break ties; it
follows creation order, not completion order. Pending records are never evicted by
this limit. Initialization and send-state transactions prune excess terminal records
without removing message history. Retained records
include the original encrypted request and remain queryable after restart;
`getOutbox` includes retained direct-message terminal records unless filtered out.

`getSendStatus` returns `undefined` for unknown or evicted records. Absence does not
prove success, failure, or that a message was never recorded locally. Previously
discarded results cannot be reconstructed from message history alone. Use status
events for progress and stored messages for history; the bounded outbox is not a
permanent delivery ledger.

## Receive and query

`messageReceived` carries received messages; `timelineChanged` indicates local
timeline changes. Use `getMessage({ sender, messageId })` for one message and
`getMessageHistory(peerAccountId?)` for a disposable local snapshot reader.
History is ordered by ascending `MessageInfo.localSequence`, including when
messages arrive with older creation times. `getMessage` and `messageReceived`
return the same persisted position. Positions belong to this database's
account-message stream, may have gaps, and cannot be compared across databases
or with relay timeline sequences. `createdAt` remains available for display.
The query does not fetch missing relay history; use [synchronization](synchronization.md)
for a fresh pass. Contacts must be authorized at receive time. Messages discarded
for missing authorization are not replayed by later contact synchronization.

Use [conversations](conversations.md) for summaries and unread counts, and
[storage and pagination](storage-and-pagination.md) for reader lifetimes.

[All guides](../README.md)
