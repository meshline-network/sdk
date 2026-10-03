import { afterEach, expect, test } from 'vitest';
import { ChannelManager, channelPostCodec, type QueryReader } from '@meshline/sdk';
import { ChannelNetwork } from '../support/channel-network.js';
const networks: ChannelNetwork[] = []; afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
async function fixture() { const network = new ChannelNetwork(); networks.push(network); const owner = await network.client(31); const peer = await network.client(32); return { network, owner, peer, relayId: network.network.descriptor.relayId }; }
async function all<T>(query: Promise<QueryReader<T>>): Promise<readonly T[]> { const reader = await query; try { return await reader.readNext(100); } finally { await reader.dispose(); } }
test('ChannelManager creates, updates, publishes, edits, reports, deletes and closes with verified local projections', async () => {
    const { network, owner, peer, relayId } = await fixture(); const created = await owner.channels.createChannel(relayId, '公开频道', { description: '简介', moderators: [peer.accountId] });
    expect(created.isFollowed).toBe(false); expect(created.descriptor!.revision).toBe(0);
    const updated = await owner.channels.updateChannel(created.ref, { name: '新名称', description: null }); expect(updated.descriptor!.name).toBe('新名称'); expect(updated.descriptor!.description).toBeUndefined();
    const post = await peer.channels.publishPost(created.ref, { body: { contentType: 'text/plain', text: '版主发布' } }); expect(post.author).toBe(peer.accountId); expect(post.ref.sequence).toBe(2);
    const edited = await owner.channels.editPost(post.ref, { body: { contentType: 'text/plain', text: '管理员修订' } }); expect(edited.body!.text).toBe('管理员修订'); expect(edited.author).toBe(peer.accountId);
    await peer.channels.reportPost(post.ref, '测试举报流程'); expect(network.reports).toHaveLength(1);
    await owner.channels.deletePost(post.ref); expect(await all(owner.channels.getPosts())).toEqual([]);
    await owner.channels.closeChannel(created.ref); expect((await owner.channels.getChannel(created.ref)).descriptor!.status).toBe('closed');
    await expect(owner.channels.publishPost(created.ref, { body: { contentType: 'text/plain', text: 'closed' } })).rejects.toThrow('closed'); expect(owner.channels).toBeInstanceOf(ChannelManager);
});
test('following synchronizes locally, supports fixed readers and stops from timeline observers', async () => {
    const { network, owner, peer, relayId } = await fixture(); const channel = await owner.channels.createChannel(relayId, 'follow');
    await peer.channels.follow(channel.ref); expect(await all(peer.channels.getFollowed())).toHaveLength(1); let stopped = false;
    peer.channels.on('timelineChanged', async () => { await peer.channels.stop(); stopped = true; });
    const published = await owner.channels.publishPost(channel.ref, { body: { contentType: 'text/plain', text: 'notification catch-up' } }); await peer.channels.start();
    await network.network.until(async () => stopped); expect((await all(peer.channels.getPosts({ channelId: channel.ref.channelId })))[0]!.messageId).toBe(published.messageId);
    await peer.channels.unfollow(channel.ref); expect(await all(peer.channels.getFollowed())).toEqual([]); expect(peer.channels.lastBackgroundError).toBeUndefined();
});
test('only owners change descriptors while unfollowed readers can load history using stable raw-event cursors', async () => {
    const { owner, peer, relayId } = await fixture(); const channel = await owner.channels.createChannel(relayId, 'history');
    await expect(peer.channels.updateChannel(channel.ref, { name: 'unauthorized' })).rejects.toThrow('not permitted');
    const post = await owner.channels.publishPost(channel.ref, { body: { contentType: 'text/plain', text: 'first' } }); await owner.channels.editPost(post.ref, { body: { contentType: 'text/plain', text: 'second' } });
    const latest = await peer.channels.loadChannelHistory(channel.ref, { limit: 1 }); expect(latest.items).toEqual([]); expect(latest.nextCursor).toBe('2');
    const previous = await peer.channels.loadChannelHistory(channel.ref, { cursor: latest.nextCursor!, limit: 1 }); expect(previous.items[0]!.body!.text).toBe('second'); expect(previous.nextCursor).toBe('1');
    expect(() => peer.channels.loadChannelHistory(channel.ref, { cursor: '-1' })).toThrow('cursor');
});
test('lost descriptor acknowledgement recovers from the exact historical revision after restart', async () => {
    const { network, owner, relayId } = await fixture(); network.loseResponse = 'channel.create'; await expect(owner.channels.createChannel(relayId, 'recover descriptor')).rejects.toThrow('response lost');
    const pending = (await owner.store.read([{ collection: 'channel_operations' }])).sets[0]![0]!; const channelId = String(pending.value.channelId); await owner.dispose();
    network.loseResponse = undefined; const resumed = await network.client(31, owner.path); await resumed.channels.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'channel_operations' }])).sets[0]!.length);
    expect(network.requests.filter(request => request.method === 'channel.create')).toHaveLength(1); expect((await resumed.channels.getChannel({ channelId, relayId })).descriptor!.name).toBe('recover descriptor');
});
test('lost post acknowledgement recovers from verified timeline evidence without another publication', async () => {
    const { network, owner, relayId } = await fixture(); const channel = await owner.channels.createChannel(relayId, 'recover publication'); network.loseResponse = 'channel.post';
    await expect(owner.channels.publishPost(channel.ref, { body: { contentType: 'text/plain', text: 'exact request' } })).rejects.toThrow('response lost');
    const pending = (await owner.store.read([{ collection: 'channel_operations' }])).sets[0]![0]!; const expected = channelPostCodec.decode(pending.value.payload!); await owner.dispose();
    network.loseResponse = undefined; const resumed = await network.client(31, owner.path); await resumed.channels.start(); await network.network.until(async () => !(await resumed.store.read([{ collection: 'channel_operations' }])).sets[0]!.length);
    expect(network.requests.filter(request => request.method === 'channel.post')).toHaveLength(1); expect((await all(resumed.channels.getPosts()))[0]!.messageId).toBe(expected.messageId);
});
test('positive post acceptance survives unavailable history and is queried after restart without resending', async () => {
    const { network, owner, relayId } = await fixture(); const channel = await owner.channels.createChannel(relayId, 'accepted'); network.failRead = true;
    await expect(owner.channels.publishPost(channel.ref, { body: { contentType: 'text/plain', text: 'retain ACK' } })).rejects.toThrow('History unavailable');
    expect((await owner.store.read([{ collection: 'channel_operations' }])).sets[0]![0]!.value.acceptedSequence).toBe(1); await owner.dispose(); network.failRead = false;
    const resumed = await network.client(31, owner.path); await resumed.channels.start(); await network.network.until(async () => !(await resumed.store.read([{ collection: 'channel_operations' }])).sets[0]!.length);
    expect(network.requests.filter(request => request.method === 'channel.post')).toHaveLength(1); expect((await all(resumed.channels.getPosts()))[0]!.body!.text).toBe('retain ACK');
});
test('lost edit and deletion responses are reconciled by signed accepted events', async () => {
    const { network, owner, relayId } = await fixture(); const channel = await owner.channels.createChannel(relayId, 'recover edits'); const post = await owner.channels.publishPost(channel.ref, { body: { contentType: 'text/plain', text: 'initial' } });
    network.loseResponse = 'channel.post.edit'; await expect(owner.channels.editPost(post.ref, { body: { contentType: 'text/plain', text: 'accepted edit' } })).rejects.toThrow('response lost'); network.loseResponse = undefined;
    await owner.channels.start(); await network.network.until(async () => !(await owner.store.read([{ collection: 'channel_operations' }])).sets[0]!.length); await owner.channels.stop();
    expect((await all(owner.channels.getPosts()))[0]!.body!.text).toBe('accepted edit'); expect(network.requests.filter(request => request.method === 'channel.post.edit')).toHaveLength(1);
    network.loseResponse = 'channel.post.delete'; await expect(owner.channels.deletePost(post.ref)).rejects.toThrow('response lost'); network.loseResponse = undefined; await owner.channels.start();
    await network.network.until(async () => !(await owner.store.read([{ collection: 'channel_operations' }])).sets[0]!.length); expect(await all(owner.channels.getPosts())).toEqual([]); expect(network.requests.filter(request => request.method === 'channel.post.delete')).toHaveLength(1);
});

test('an incorrect publication sequence stays pending with a visible failure and is never automatically posted again', async () => {
    const { network, owner, relayId } = await fixture(); const channel = await owner.channels.createChannel(relayId, 'bad receipt'); network.wrongSequence = true;
    await expect(owner.channels.publishPost(channel.ref, { body: { contentType: 'text/plain', text: 'must not duplicate' } })).rejects.toThrow('absent');
    expect((await owner.store.read([{ collection: 'channel_operations' }])).sets[0]![0]!.value.acceptedSequence).toBe(101);
    await owner.channels.start(); await network.network.until(async () => owner.channels.lastBackgroundError !== undefined); await owner.channels.stop();
    expect(network.requests.filter(request => request.method === 'channel.post')).toHaveLength(1); expect((await owner.store.read([{ collection: 'channel_operations' }])).sets[0]!).toHaveLength(1);
});
