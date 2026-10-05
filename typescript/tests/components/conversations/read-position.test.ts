import { afterEach, expect, test, vi } from 'vitest';
import { MeshlineClient, type QueryReader } from '@meshline/sdk';
import { ClientNetwork } from '../../support/client-network.js';
import { ChannelNetwork } from '../../support/channel-network.js';
import { messageDevice } from '../../support/message-fixture.js';
import { context } from '../../support/relay-fixture.js';
import { createConversationFixture } from '../../support/conversation-fixture.js';

const resources: { dispose(): Promise<void> }[] = [];
afterEach(async () => {
    vi.restoreAllMocks();
    for (const resource of resources.splice(0).reverse()) await resource.dispose();
});
async function all<T>(query: Promise<QueryReader<T>>) {
    const reader = await query;
    try { return await reader.readNext(100); } finally { await reader.dispose(); }
}
function fixture() {
    const network = new ClientNetwork();
    resources.push(network);
    return createConversationFixture(network);
}

test.each([false, true])('interleaved direct conversations keep independent read positions (explicit position: %s)', async explicitPosition => {
    const f = await fixture(); const first = f.peer.certificate.account; const peer = messageDevice(context, 96, 97); const second = peer.certificate.account;
    await f.incoming('A1'); const b1 = await f.incoming('B1', f.network.clock.wall, peer);
    const a2 = await f.incoming('A2'); const b2 = await f.incoming('B2', f.network.clock.wall, peer);
    expect(b1.localSequence).toBeLessThan(a2.localSequence); expect(a2.localSequence).toBeLessThan(b2.localSequence);
    if (explicitPosition) await f.client.markRead(first, a2.localSequence); else await f.client.markRead(first);
    expect((await f.client.getConversation(first))!.unreadCount).toBe(0); expect((await f.client.getConversation(second))!.unreadCount).toBe(2);
    await f.client.markRead(second, b1.localSequence); expect((await f.client.getConversation(second))!.unreadCount).toBe(1);
    await f.incoming('A3'); await f.client.markRead(second);
    await f.dispose(); const resumed = await f.network.open(91, f.path);
    expect((await resumed.client.getConversation(first))!.unreadCount).toBe(1); expect((await resumed.client.getConversation(second))!.unreadCount).toBe(0);
    const unread = await all(resumed.client.getConversations({ unreadOnly: true })); expect(unread.map(value => value.conversationId)).toEqual([first]);
    const positions = (await resumed.store.read([{ collection: 'conversation_reads' }])).sets[0]!;
    expect(positions).toHaveLength(2); expect(Object.fromEntries(positions.map(row => [row.key, row.value.sequence]))).toEqual({ [first]: a2.localSequence, [second]: b2.localSequence });
});

test('mark-read preserves the supplied boundary on CAS contention and leaves concurrent arrivals unread', async () => {
    const f = await fixture(); const read = await f.incoming('before'); const commit = f.store.commit.bind(f.store); let inject = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (inject && mutations.some(value => value.collection === 'conversation_reads')) { inject = false; await f.incoming('concurrent'); }
        return commit(version, mutations, signal);
    });
    await f.client.markRead(f.peer.certificate.account, read.localSequence); const current = (await f.client.getConversation(f.peer.certificate.account))!;
    expect(current.latest!.text).toBe('concurrent'); expect(current.unreadCount).toBe(1); expect((await f.store.read([{ collection: 'conversation_reads' }])).sets[0]![0]!.value.sequence).toBe(read.localSequence);
    await f.incoming('after'); expect((await f.client.getConversation(f.peer.certificate.account))!.unreadCount).toBe(2);
});

test('mark-read without a position includes the latest arrival when a storage write retries', async () => {
    const f = await fixture(); await f.incoming('before'); const commit = f.store.commit.bind(f.store); let inject = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (inject && mutations.some(value => value.collection === 'conversation_reads')) { inject = false; await f.incoming('concurrent'); }
        return commit(version, mutations, signal);
    });
    await f.client.markRead(f.peer.certificate.account); const current = (await f.client.getConversation(f.peer.certificate.account))!;
    expect(inject).toBe(false); expect(current.latest!.text).toBe('concurrent'); expect(current.unreadCount).toBe(0);
    await f.incoming('after'); expect((await f.client.getConversation(f.peer.certificate.account))!.unreadCount).toBe(1);
});

test('mark-read without a position handles empty conversations and the original cancellation argument', async () => {
    const f = await fixture(); const id = f.peer.certificate.account;
    await f.client.markRead(id); expect((await f.store.read([{ collection: 'conversation_reads' }])).sets[0]).toEqual([]);
    const viewed = await f.incoming('viewed'); await f.incoming('arrived after viewing');
    await f.client.markRead(id, viewed.localSequence); expect((await f.client.getConversation(id))!.unreadCount).toBe(1);
    const cancellation = new AbortController(); cancellation.abort();
    await expect(f.client.markRead(id, cancellation.signal)).rejects.toThrow(); expect((await f.client.getConversation(id))!.unreadCount).toBe(1);
    await f.client.markRead(id, new AbortController().signal);
    await f.client.markRead(id, viewed.localSequence); await f.client.markRead(id);
    expect((await f.client.getConversation(id))!.unreadCount).toBe(0);
});

test('a deleted channel boundary excludes later publications and rejects deletion-event positions', async () => {
    const network = new ChannelNetwork(); resources.push(network); const owner = await network.client(85); const base = await network.client(86);
    const client = new MeshlineClient({ context, accountId: base.accountId, store: base.store, relayClients: base.pool, secretProtector: base.protector, clock: network.network.clock }); resources.push(client); await client.initialize();
    const channel = (await owner.channels.createChannel(network.network.descriptor.relayId, 'Read boundary')).ref; await client.channelManager.follow(channel);
    await expect(client.markRead(channel.channelId, 1)).rejects.toThrow('locally known message');
    await owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'earlier' } });
    const boundary = await owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'viewed' } }); await client.channelManager.loadChannelHistory(channel);
    const viewed = (await all(client.channelManager.getPosts({ channelId: channel.channelId }))).at(-1)!;
    await owner.channels.deletePost(boundary.ref);
    const late = await owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'unread arrival' } }); await client.channelManager.loadChannelHistory(channel);
    await expect(client.markRead(channel.channelId, boundary.localSequence + 1)).rejects.toThrow('locally known message');
    await client.markRead(channel.channelId, viewed.localSequence); expect((await client.getConversation(channel.channelId))!.unreadCount).toBe(1);
    await client.markRead(channel.channelId, late.localSequence); await client.markRead(channel.channelId, viewed.localSequence);
    expect((await client.getConversation(channel.channelId))!.unreadCount).toBe(0);
});

test('partial snapshot reads leave later arrivals unread', async () => {
    const f = await fixture();
    const id = f.peer.certificate.account;
    await f.incoming('first');
    await f.incoming('second');
    const reader = await f.client.messageManager.getMessageHistory(id);
    await f.incoming('after snapshot');
    try {
        const batch = await reader.readNext(1);
        await f.client.markRead(id, batch[0]!.localSequence);
        expect((await f.client.getConversation(id))!.unreadCount).toBe(2);
        await f.client.markRead(id, (await reader.readNext(1))[0]!.localSequence);
        expect(await reader.readNext(1)).toEqual([]);
        expect((await f.client.getConversation(id))!.unreadCount).toBe(1);
    } finally { await reader.dispose(); }
});

test.each([-1, 0, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1, 'future'] as const)('invalid read boundary %s leaves unread state unchanged', async invalid => {
    const f = await fixture();
    const viewed = await f.incoming('viewed');
    const late = await f.incoming('late');
    await f.client.markRead(f.peer.certificate.account, viewed.localSequence);
    const position = invalid === 'future' ? late.localSequence + 1 : invalid;
    await expect(f.client.markRead(f.peer.certificate.account, position)).rejects.toThrow();
    expect((await f.client.getConversation(f.peer.certificate.account))!.unreadCount).toBe(1);
});

test('a boundary from another conversation cannot advance the local read position', async () => {
    const f = await fixture();
    const id = f.peer.certificate.account;
    await f.incoming('unread');
    const stranger = messageDevice(context, 96, 97);
    const unrelated = await f.incoming('other conversation', f.network.clock.wall, stranger);
    await expect(f.client.markRead(id, unrelated.localSequence)).rejects.toThrow('locally known message');
    expect((await f.client.getConversation(id))!.unreadCount).toBe(1);
});

test('concurrent acknowledgments only advance their own conversation', async () => {
    const f = await fixture();
    const id = f.peer.certificate.account;
    const first = await f.incoming('first');
    const second = await f.incoming('second');
    const late = await f.incoming('late');
    const stranger = messageDevice(context, 96, 97);
    await f.incoming('other conversation', f.network.clock.wall, stranger);
    await Promise.all([f.client.markRead(id, late.localSequence), f.client.markRead(id, first.localSequence), f.client.markRead(id, second.localSequence)]);
    expect((await f.client.getConversation(id))!.unreadCount).toBe(0);
    expect((await f.client.getConversation(stranger.certificate.account))!.unreadCount).toBe(1);
});
