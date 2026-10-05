# Channels

[Guide index](../README.md) · [Groups](groups.md)

## Create and publish

Channels carry public posts. Select an eligible hosting relay in the same network and use an initialized, authorized client. Creation and management require the authority checked by the corresponding operation. Use `ChannelRef` to preserve both the channel ID and hosting relay.

<!-- snippet: channel -->
```csharp
public static async Task<ChannelInfo> CreateChannelAsync(
    MeshlineClient client, string relayId, CancellationToken cancellationToken = default)
{
    var channel = await client.ChannelManager.CreateChannelAsync(
        relayId, "Announcements", cancellationToken: cancellationToken);
    await client.ChannelManager.FollowAsync(channel.Ref, cancellationToken);
    await client.ChannelManager.PublishPostAsync(channel.Ref, new ChannelPostDraft
    {
        Body = new MessageBody { ContentType = "text/plain", Text = "Welcome!" }
    }, cancellationToken);
    return channel;
}
```
<!-- /snippet -->

Source: [Spaces.cs](../../examples/Meshline.Sdk.Examples/Spaces.cs). Following controls this device's local following state and background synchronization. `UnfollowAsync` removes that follow; it does not close the channel for other users.

## Read history and local posts

`LoadChannelHistoryAsync` asks the hosting relay for retained history and persists returned posts. Pass the returned cursor unchanged to load the next page:

<!-- snippet: channel-history -->
```csharp
public static async Task LoadChannelHistoryAsync(
    MeshlineClient client, ChannelRef channel, CancellationToken cancellationToken = default)
{
    string? cursor = null;
    do
    {
        var page = await client.ChannelManager.LoadChannelHistoryAsync(
            channel, new PageRequest { Limit = 50, Cursor = cursor }, cancellationToken);
        foreach (var post in page.Items)
            Console.WriteLine(post.Body?.Text);
        cursor = page.NextCursor;
    } while (cursor is not null);
}
```
<!-- /snippet -->

An interactive UI can request one page at a time instead of exhausting history in a loop. Stop when `NextCursor` is `null`. Treat the cursor as opaque and specific to its channel/query. `GetPostsAsync` reads local posts through a snapshot reader; it does not itself fetch missing history.

`ChannelChanged` reports descriptor changes, `TimelineChanged` reports post changes, and `FollowChanged` reports local following changes. WebSocket notifications accelerate HTTP synchronization; reconnecting and restoring subscriptions triggers catch-up. Query local state again after changes rather than assuming a notification contains the whole timeline.

## Manage a channel

`UpdateChannelAsync` changes descriptor fields. `EditPostAsync` and `DeletePostAsync` take a `ChannelPostRef`, which identifies the actual post. `ReportPostAsync` submits a reason for a post report. Relay authorization and validation still apply; the existence of a method does not grant moderation privileges to every caller.

`CloseChannelAsync` closes the channel and is distinct from unfollowing. Public channel content has different privacy properties from encrypted direct messages and groups. Applications must account for relay retention and deletion semantics when displaying historical posts; a local cache is not proof that a post is still retrievable from the network.

For foreground refreshes and completion status, see [synchronization](synchronization.md).

## API reference

[ChannelManager](../api/Meshline.Components.ChannelManager.md) · [ChannelRef](../api/Meshline.Models.Client.ChannelRef.md) · [ChannelPostRef](../api/Meshline.Models.Client.ChannelPostRef.md) · [ChannelUpdate](../api/Meshline.Models.Client.ChannelUpdate.md) · [ChannelPostUpdate](../api/Meshline.Models.Client.ChannelPostUpdate.md)
