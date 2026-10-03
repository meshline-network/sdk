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

Call `await client.markRead(conversationId)` after the user reads the stored
conversation. It advances to the latest currently stored readable item atomically.
Repeated calls cannot move backward or mark future arrivals as read.

Unread counts exclude messages sent by the current account. Read positions are
local state; they do not claim another person read a message.
`conversationChanged` identifies a conversation and whether it was created,
updated, or removed. Re-query its snapshot to refresh the UI.

These queries use local data. They do not request missing relay history.
See [storage and pagination](storage-and-pagination.md).

[All guides](../README.md)
