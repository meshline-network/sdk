# Conversations and unread state

`MeshlineClient` projects locally stored direct messages, groups, and followed
channels into one conversation list. A conversation ID is the peer's account ID
for direct messages, the group ID for groups, or the channel ID for channels.

## Read a conversation list

This function from [workflows.ts](../../examples/workflows.ts) reads the first page
of unread direct and group conversations:

```ts
export async function unreadConversations(client: MeshlineClient) {
    const reader = await client.getConversations({ kinds: ['direct', 'group'], unreadOnly: true });
    try {
        return await reader.readNext(50);
    } finally {
        await reader.dispose();
    }
}
```

Omit `kinds` to include all kinds; an empty array matches none.
`unreadOnly` defaults to false. Set `hasMessages` to include only conversations
with messages or only empty ones. `getConversation(conversationId)` returns an
optional local snapshot.

The list is ordered by the latest summary timestamp descending, then conversation
ID, with empty conversations last. Within a conversation, the latest item follows
accepted/local sequence order; a sender timestamp cannot make an older item its
head. The reader is a fixed snapshot. Open a new reader to show new arrivals.

## Track read positions

Call `await client.markRead(conversationId, message.localSequence)` after the
application has actually viewed the messages through that position.
The boundary is inclusive and cumulative: acknowledging 100 leaves messages beyond
100 unread, including arrivals between querying and marking or during a write retry.
Repeated or older positions are no-ops; concurrent acknowledgments cannot move backward.

`await client.markRead(conversationId)` accepts an optional cancellation argument:
`client.markRead(conversationId, signal)`.
It marks through the latest locally stored readable message when the operation
executes, recomputing that position if a storage write retries. Use it for a "mark
all as read" action; it can include arrivals since an earlier query. Empty
conversations are no-ops, and the read position never moves backward. Use the
explicit `localSequence` overload when confirming a batch the application has viewed.

The position must be a positive safe integer. Advancing requires a locally stored
direct message, decrypted group message, or known original channel publication at
that position. Unknown or future positions are rejected. A deleted channel post
remains a valid boundary while its original publication metadata is retained.

Use positions from messages actually viewed in one conversation. An empty batch
supplies no new boundary; reading one batch does not acknowledge later batches.
A sender filter or incomplete history does not establish that all earlier messages
were viewed. For groups and channels, history backfilled or decrypted later at or
below the acknowledged position is part of the already acknowledged prefix.

## Choose the correct position

Message models expose a common `localSequence` property for the position used by
the conversation's local read state:

| Message model | `localSequence` |
| --- | --- |
| `MessageInfo` | The sequence assigned in this database's account-message stream. |
| `GroupMessageInfo` | A read-only alias of `sequence`, assigned by the hosting relay within that group. |
| `ChannelPostInfo` | A read-only alias of `ref.sequence`, the original publication position within that channel. Edits retain this position. |

Keep the conversation identifier with the position. Values may have gaps, and
group/channel aliases retain their original timeline scope rather than allocating
another local counter.

Direct messages share an account-wide `localSequence`, but each peer's conversation
stores its own read position. Both `markRead` overloads update only the supplied
conversation. For example, if messages arrive as A:10, B:20, A:30, B:40, acknowledging
A through 30 leaves both B messages unread. Unread counts compare messages against
their own conversation's read position; they do not use one global read position.

## Refresh after changes

Unread counts exclude messages sent by the current account. Read positions are
local state; they do not claim another person read a message or provide an exact
application-processing ACK.
`conversationChanged` identifies a conversation and whether it was created,
updated, or removed. Re-query its snapshot to refresh the UI.

These queries use local data. They do not request missing relay history.
See [storage and pagination](storage-and-pagination.md).

[All guides](../README.md)
