# Channels

Channels contain public posts hosted by a relay. Use `channelManager` with a
`ChannelRef` containing `channelId` and `relayId`. A post reference adds its
accepted sequence.

## Create and publish

This function from [workflows.ts](../../examples/workflows.ts) creates a channel,
publishes a post, and edits it:

```ts
export async function publishAnnouncement(client: MeshlineClient, relayId: string) {
    const channel = await client.channelManager.createChannel(relayId, 'Announcements');
    const post = await client.channelManager.publishPost(channel.ref, {
        body: { contentType: 'text/plain', text: 'First post' },
    });
    return client.channelManager.editPost(post.ref, {
        body: { contentType: 'text/plain', text: 'Updated post' },
    });
}
```

Channel creation accepts optional description and moderators.
`updateChannel` changes metadata; `closeChannel` closes the channel.
Mutation permissions depend on the verified channel state and the caller's role.

In updates, omitted fields stay unchanged. `null` clears fields whose update type
permits it: description/moderators in channel metadata, and body/attachments in a
post. Supply a complete replacement value to replace one of those fields.
`deletePost` records deletion; `reportPost` submits a report with a reason.

## Follow and load history

`follow(channel)` resolves the channel and records the local follow.
A running client synchronizes followed channels through subscriptions and polling.
`unfollow(channel)` removes the follow. Use `getFollowed(relayId?)` for a local
snapshot reader and `getChannel(channel)` to resolve channel information.

`loadChannelHistory(channel, { limit, cursor })` fetches and verifies retained
relay history and stores the resulting changes. Omit the cursor for the latest
page, then pass the returned `nextCursor` to load older history. Stop when
`nextCursor` is absent. Cursors are canonical decimal strings; do not parse and
reformat them or reuse them for a different channel.

`getPosts({ channelId, author })` reads locally stored posts and does not fetch
remote pages. Its snapshot is ordered by channel ID and sequence. Dispose the
reader when finished.

## Handle changes and interruption

Observe `channelChanged`, `followChanged`, and `timelineChanged`.
Verified signed history governs descriptors, post edits, deletion, and historical
permissions. Refresh stored snapshots after changes instead of assuming every
notification is a newly created post.

A lost response may leave an accepted publication pending locally. The SDK retains
the original signed request and reconciles verified history; known acceptance is
not reposted. Inconsistent acceptance evidence remains a visible failure rather
than causing a new publication. Keep pending state across restarts.

For foreground refreshes and completion status, see [synchronization](synchronization.md).

[All guides](../README.md)
