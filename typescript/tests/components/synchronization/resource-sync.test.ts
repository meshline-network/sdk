import { afterEach, expect, test } from 'vitest';
import { type QueryReader, type ResourceSyncStatus } from '@meshline/sdk';
import { MessagingNetwork } from '../../support/messaging-network.js';
import { GroupNetwork } from '../../support/group-network.js';
import { ChannelNetwork } from '../../support/channel-network.js';

const networks: { dispose(): Promise<void> }[] = [];
afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
function response(value: unknown, status = 200): Response { return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } }); }
async function all<T>(query: Promise<QueryReader<T>>): Promise<readonly T[]> { const reader = await query; try { return await reader.readNext(100); } finally { await reader.dispose(); } }

test('empty account timelines retain gaps across restart but completion is runtime only', async () => {
    const network = new MessagingNetwork(); networks.push(network); const a = await network.client(21); const relayId = network.descriptor.relayId;
    const changes: ResourceSyncStatus[] = []; a.messages.on('syncStatusChanged', status => { changes.push(status); });
    expect(await a.messages.getSyncStatus(relayId)).toEqual({ resource: relayId, state: 'idle', hasRetentionGap: false });
    network.handleRequest = async method => method === 'message.timeline.sync' ? response({ items: [], certificates: [], has_more: false, has_retention_gap: true }) : undefined;
    await a.messages.start(); await network.until(async () => (await a.messages.getSyncStatus(relayId)).state === 'caughtUp');
    expect(await a.messages.getSyncStatus(relayId)).toMatchObject({ state: 'caughtUp', hasRetentionGap: true, lastSynchronizedAt: network.clock.wall });
    expect(changes.some(status => status.state === 'synchronizing')).toBe(true);
    await a.messages.stop(); expect((await a.messages.getSyncStatus(relayId)).state).toBe('idle');
    await a.dispose(); const resumed = await network.client(21, a.path);
    expect(await resumed.messages.getSyncStatus(relayId)).toEqual({ resource: relayId, state: 'idle', hasRetentionGap: true });
});

test('a failed account sync has no completion timestamp and retry clears current error and reason', async () => {
    const network = new MessagingNetwork(); networks.push(network); const a = await network.client(21); const relayId = network.descriptor.relayId; let fail = true;
    network.handleRequest = async method => method === 'message.timeline.sync' && fail ? response({ code: 'forbidden', message: 'denied' }, 403) : undefined;
    await a.messages.start(); await network.until(async () => (await a.messages.getSyncStatus(relayId)).state === 'blocked');
    const blocked = await a.messages.getSyncStatus(relayId); expect(blocked.blockReason).toBe('permission'); expect(blocked.error).toBeDefined(); expect(blocked.lastSynchronizedAt).toBeUndefined();
    fail = false; network.clock.tick();
    await network.until(async () => (await a.messages.getSyncStatus(relayId)).state === 'caughtUp');
    expect((await a.messages.getSyncStatus(relayId)).error).toBeUndefined(); expect((await a.messages.getSyncStatus(relayId)).blockReason).toBeUndefined();
});

test('missing group keys block readable messages, recover after restoration, and exclude pre-join history', async () => {
    const network = new GroupNetwork(); networks.push(network); const owner = await network.client(51); const peer = await network.client(52); const relayId = network.network.descriptor.relayId;
    const group = (await owner.groups.createGroup(relayId, { name: 'key status', memberCapacity: 4 })).ref;
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'before membership' } });
    const invite = await owner.groups.createInvite(group, { invitee: peer.accountId, expiresAt: network.network.clock.wall + 3600 });
    await peer.groups.applyToGroup(invite); await owner.groups.approveApplications(group, [peer.accountId]); await peer.groups.getGroup(group);
    expect((await peer.groups.getSyncStatus(group.groupId)).state).toBe('caughtUp');
    const previous = await peer.groups.getSyncStatus(group.groupId);
    const saved = await peer.store.read([{ collection: 'group_member_keys' }, { collection: 'group_application_secrets' }, { collection: 'group_client_secrets' }]);
    await peer.store.commit(saved.version, saved.sets.flat().map(row => ({ kind: 'delete', collection: row.collection, key: row.key })));
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'after membership' } });
    const blocked = await peer.groups.synchronize(group);
    expect(blocked).toMatchObject({ state: 'blocked', blockReason: 'missingKey', lastSynchronizedAt: previous.lastSynchronizedAt });
    expect(await peer.groups.getSyncStatus(group.groupId)).toBe(blocked);
    const current = await peer.store.read([]);
    await peer.store.commit(current.version, saved.sets[0]!.map(row => ({ kind: 'put', collection: row.collection, key: row.key, value: row.value })));
    network.network.clock.wall += 1;
    const complete = await peer.groups.synchronize(group); expect(complete.state).toBe('caughtUp'); expect(complete.blockReason).toBeUndefined();
    expect(complete.lastSynchronizedAt).toBeGreaterThan(previous.lastSynchronizedAt!);
    expect((await all(peer.groups.getMessages())).map(message => message.body!.text)).toEqual(['after membership']);
});

test('history pages do not claim complete sync and followed channel failures recover', async () => {
    const network = new ChannelNetwork(); networks.push(network); const owner = await network.client(31); const peer = await network.client(32);
    const channel = (await owner.channels.createChannel(network.network.descriptor.relayId, 'sync status')).ref;
    await peer.channels.loadChannelHistory(channel, { limit: 1 });
    expect((await peer.channels.getSyncStatus(channel.channelId)).state).toBe('idle');
    await peer.channels.follow(channel); const handle = network.network.handleRequest!; let fail = true;
    network.network.handleRequest = async (method, ...args) => method === 'channel.read' && fail ? response({ code: 'forbidden', message: 'denied' }, 403) : handle(method, ...args);
    await peer.channels.start(); await network.network.until(async () => (await peer.channels.getSyncStatus(channel.channelId)).state === 'blocked');
    expect((await peer.channels.getSyncStatus(channel.channelId)).blockReason).toBe('permission');
    fail = false; network.network.clock.tick();
    await network.network.until(async () => (await peer.channels.getSyncStatus(channel.channelId)).state === 'caughtUp');
    expect((await peer.channels.getSyncStatus(channel.channelId)).blockReason).toBeUndefined();
    await peer.channels.stop(); expect((await peer.channels.getSyncStatus(channel.channelId)).state).toBe('idle');
});
