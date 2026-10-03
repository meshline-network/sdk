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
| `targetAccepted` | The protocol reports target acceptance; this is not a user read receipt. |
| `failed` | Processing recorded a terminal failure; inspect `errorMessage`. |
| `canceled` | A queued request was canceled. |

Status includes message and recipient IDs, creation time, and acceptance/error
details when available. `getOutbox({ recipient, states })` opens a local snapshot:
omitting `states` includes all states; `states: []` includes none.

## Cancel or recover

`cancelMessage(messageId)` returns whether it canceled a queued message. Once
submission has begun, cancellation cannot retract remote acceptance. An
`AbortSignal` cancels the caller's operation; it does not prove the relay rejected
or rolled back a request.

For uncertain submission, keep the original database and protector. The SDK
retains the original ciphertext and accepting relay identity for retry or status
reconciliation, including across process restart. Sending a newly created message
as a generic retry can duplicate the user's message.

## Receive and query

`messageReceived` carries received messages; `timelineChanged` indicates local
timeline changes. Use `getMessage({ sender, messageId })` for one message and
`getMessageHistory(peerAccountId?)` for a disposable local snapshot reader.
History is ordered by creation time, then message ID and sender ID. It does not
fetch missing relay history.

Use [conversations](conversations.md) for summaries and unread counts, and
[storage and pagination](storage-and-pagination.md) for reader lifetimes.

[All guides](../README.md)
