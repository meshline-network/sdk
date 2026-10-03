import { afterEach, expect, test, vi } from 'vitest';
import { MeshlineClient, createIdentifier, decryptMessage, directMessageCodec, encryptMessage, type ConversationChange, type QueryReader } from '@meshline/sdk';
import { MessageRepository } from '../../packages/sdk/dist/messages/repository.js';
import { ClientNetwork } from '../support/client-network.js';
import { GroupNetwork } from '../support/group-network.js';
import { ChannelNetwork } from '../support/channel-network.js';
import { messageDevice } from '../support/message-fixture.js';
import { context } from '../support/relay-fixture.js';
const resources: { dispose(): Promise<void> }[] = []; afterEach(async () => { vi.restoreAllMocks(); for (const resource of resources.splice(0).reverse()) await resource.dispose(); });
async function all<T>(query: Promise<QueryReader<T>>) { const reader = await query; try { return await reader.readNext(100); } finally { await reader.dispose(); } }
async function fixture() {
    const network = new ClientNetwork(); resources.push(network); const local = await network.open(); await local.client.establishAccount(); network.clock.wall += 60;
    const peer = messageDevice(context, 94, 95); let sequence = 0;
    const repository = new MessageRepository({ store: local.store, context, accountId: local.client.accountId, deviceId: () => '', clock: network.clock });
    async function incoming(text: string, createdAt = network.clock.wall, sender = peer) {
        const payload = directMessageCodec.encode({ body: { contentType: 'text/plain', text } }); const receiver = local.client.deviceManager;
        const message = await encryptMessage({ context, signer: sender, messageId: createIdentifier('message'), createdAt, recipient: local.client.accountId, recipientDevices: [receiver.certificate], payload });
        const decoded = await decryptMessage({ context, receiver, envelope: message.envelope, keyBox: message.recipientBoxes[0]!, sender: sender.certificate });
        await repository.accept(network.relays[0]!.descriptor.relayId, { sequence: sequence++, acceptedAt: network.clock.wall, envelope: message.envelope, keyBox: message.recipientBoxes[0]! }, await repository.prepare(message.envelope, decoded), false);
    }
    return { ...local, network, peer, incoming };
}

test('direct summaries follow local acceptance order, count only incoming unread messages and survive restart', async () => {
    const f = await fixture(); await f.incoming('first'); await f.incoming('arrived later, older timestamp', f.network.clock.wall - 20);
    let conversation = (await f.client.getConversation(f.peer.certificate.account))!;
    expect(conversation.kind).toBe('direct'); expect(conversation.unreadCount).toBe(2); expect(conversation.latest!.text).toBe('arrived later, older timestamp'); expect(conversation.latest!.timestamp).toBe(f.network.clock.wall - 20);
    await f.client.markRead(conversation.conversationId); await f.incoming('new unread'); conversation = (await f.client.getConversation(conversation.conversationId))!; expect(conversation.unreadCount).toBe(1);
    const peerId = conversation.conversationId; await f.dispose(); const resumed = await f.network.open(91, f.path); expect((await resumed.client.getConversation(peerId))!.unreadCount).toBe(1);
    await resumed.client.markRead(peerId); expect((await resumed.client.getConversation(peerId))!.unreadCount).toBe(0);
});

test('query readers retain their snapshot, kind filters combine, and empty kind selection returns no conversations', async () => {
    const f = await fixture(); await f.incoming('snapshot'); const reader = await f.client.getConversations({ kinds: ['direct', 'channel'], unreadOnly: true });
    await f.client.markRead(f.peer.certificate.account); await f.incoming('new snapshot');
    try { expect((await reader.readNext(10))[0]!.latest!.text).toBe('snapshot'); expect(await reader.readNext(10)).toEqual([]); } finally { await reader.dispose(); }
    expect((await all(f.client.getConversations({ hasMessages: true })))[0]!.latest!.text).toBe('new snapshot'); expect(await all(f.client.getConversations({ hasMessages: false }))).toEqual([]); expect(await all(f.client.getConversations({ kinds: [] }))).toEqual([]);
    await expect(f.client.getConversation('invalid')).rejects.toThrow(); await expect(f.client.getConversations({ kinds: ['unexpected' as 'direct'] })).rejects.toThrow();
});

test('mark-read and its head are recomputed on CAS contention without losing a concurrent incoming item', async () => {
    const f = await fixture(); await f.incoming('before'); const commit = f.store.commit.bind(f.store); let inject = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (inject && mutations.some(value => value.collection === 'conversation_reads')) { inject = false; await f.incoming('concurrent'); }
        return commit(version, mutations, signal);
    });
    await f.client.markRead(f.peer.certificate.account); const current = (await f.client.getConversation(f.peer.certificate.account))!;
    expect(current.latest!.text).toBe('concurrent'); expect(current.unreadCount).toBe(0); expect((await f.store.read([{ collection: 'conversation_reads' }])).sets[0]![0]!.value.sequence).toBe(2);
    await f.incoming('after'); expect((await f.client.getConversation(f.peer.certificate.account))!.unreadCount).toBe(1);
});

test('outgoing self messages are immediately visible without unread counts and conversation observers may dispose the client', async () => {
    const f = await fixture(); const changes: ConversationChange[] = []; let disposed: Promise<void> | undefined;
    f.client.on('conversationChanged', value => { changes.push(value); disposed = f.client.dispose(); return disposed; });
    await f.client.messageManager.sendMessage(f.client.accountId, { body: { contentType: 'text/plain', text: 'self conversation' } });
    for (let count = 0; !disposed && count < 100; count++) await new Promise(resolve => setTimeout(resolve, 5)); await disposed;
    expect(changes).toEqual([{ conversationId: f.client.accountId, kind: 'created' }]); expect(f.client.lifecycleState).toBe('disposed');
    const resumed = await f.network.open(91, f.path); expect((await resumed.client.getConversation(f.client.accountId))!.unreadCount).toBe(0);
});

test('current membership creates an empty group conversation, while decrypted history retains it after leaving', async () => {
    const network = new GroupNetwork(); resources.push(network); const base = await network.client(81); const peer = await network.client(82);
    const client = new MeshlineClient({ context, accountId: base.accountId, store: base.store, relayClients: base.pool, secretProtector: base.protector, clock: network.network.clock }); resources.push(client); await client.initialize();
    const changes: ConversationChange[] = []; client.on('conversationChanged', value => { changes.push(value); });
    const group = (await client.groupManager.createGroup(network.network.descriptor.relayId, { name: 'Conversations', memberCapacity: 5 })).ref;
    expect(await client.getConversation(group.groupId)).toEqual({ conversationId: group.groupId, kind: 'group', unreadCount: 0 });
    const invite = await client.groupManager.createInvite(group, { expiresAt: network.network.clock.wall + 1000, invitee: peer.accountId }); await peer.groups.applyToGroup(invite); await client.groupManager.approveApplications(group, [peer.accountId]);
    await peer.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'peer group message' } }); await client.groupManager.getGroup(group);
    expect((await client.getConversation(group.groupId))!.unreadCount).toBe(1); await client.markRead(group.groupId); await client.groupManager.setNickname(group, 'not a conversation message');
    expect((await client.getConversation(group.groupId))!.latest!.text).toBe('peer group message'); expect((await client.getConversation(group.groupId))!.unreadCount).toBe(0);
    await client.groupManager.transferOwnership(group, peer.accountId); await client.groupManager.leaveGroup(group); expect((await client.getConversation(group.groupId))!.latest!.text).toBe('peer group message');
    await network.network.until(async () => changes.some(value => value.conversationId === group.groupId && value.kind === 'created'));
    const empty = (await peer.groups.createGroup(network.network.descriptor.relayId, { name: 'No history', memberCapacity: 5 })).ref; const second = await peer.groups.createInvite(empty, { expiresAt: network.network.clock.wall + 1000, invitee: base.accountId }); await client.groupManager.applyToGroup(second); await peer.groups.approveApplications(empty, [base.accountId]); await client.groupManager.getGroup(empty);
    expect(await client.getConversation(empty.groupId)).toBeDefined(); await client.groupManager.leaveGroup(empty); expect(await client.getConversation(empty.groupId)).toBeUndefined();
    await network.network.until(async () => changes.some(value => value.conversationId === empty.groupId && value.kind === 'removed'));
});

test('channel summaries exclude tombstones, edits retain the publication position, and unread state survives unfollow/refollow', async () => {
    const network = new ChannelNetwork(); resources.push(network); const owner = await network.client(83); const base = await network.client(84);
    const client = new MeshlineClient({ context, accountId: base.accountId, store: base.store, relayClients: base.pool, secretProtector: base.protector, clock: network.network.clock }); resources.push(client); await client.initialize();
    const channel = (await owner.channels.createChannel(network.network.descriptor.relayId, 'Channel conversations')).ref; await client.channelManager.follow(channel);
    expect((await client.getConversation(channel.channelId))!.latest).toBeUndefined(); const first = await owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'first' } });
    const second = await owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'second' } }); await client.channelManager.loadChannelHistory(channel);
    expect((await client.getConversation(channel.channelId))!.unreadCount).toBe(2); await client.markRead(channel.channelId);
    await owner.channels.editPost(first.ref, { body: { contentType: 'text/plain', text: 'first edited later' } }); await client.channelManager.loadChannelHistory(channel);
    expect((await client.getConversation(channel.channelId))!.latest!.text).toBe('second'); expect((await client.getConversation(channel.channelId))!.unreadCount).toBe(0);
    await owner.channels.deletePost(second.ref); await client.channelManager.loadChannelHistory(channel); expect((await client.getConversation(channel.channelId))!.latest!.text).toBe('first edited later');
    await client.channelManager.unfollow(channel); expect(await client.getConversation(channel.channelId)).toBeUndefined();
    await client.markRead(channel.channelId); await client.channelManager.follow(channel); expect((await client.getConversation(channel.channelId))!.unreadCount).toBe(0);
});

test('conversation observation retries a local read failure and reports observer rejection without blocking subsequent changes', async () => {
    const f = await fixture(); const read = f.store.read.bind(f.store); let fail = true; let notifications = 0;
    vi.spyOn(f.store, 'read').mockImplementation(async (queries, signal) => { if (fail && queries.some(value => value.collection === 'conversation_reads')) { fail = false; throw new Error('Read unavailable'); } return read(queries, signal); });
    f.client.on('conversationChanged', () => { notifications++; throw new Error('Observer rejected'); });
    await f.client.messageManager.sendMessage(f.client.accountId, { body: { contentType: 'text/plain', text: 'retry observation' } });
    for (let count = 0; notifications < 1 && count < 100; count++) { f.network.clock.tick(); await new Promise(resolve => setTimeout(resolve, 5)); }
    expect(notifications).toBe(1); expect(f.client.lastBackgroundError!.operation).toBe('observer');
    await f.client.messageManager.sendMessage(f.client.accountId, { body: { contentType: 'text/plain', text: 'observer still alive' } });
    for (let count = 0; notifications < 2 && count < 100; count++) await new Promise(resolve => setTimeout(resolve, 5)); expect(notifications).toBe(2);
});
