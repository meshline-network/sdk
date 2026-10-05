import { afterEach, expect, test } from 'vitest';
import { AsyncLocalStorage } from 'node:async_hooks';
import { accountGroupPrivateStateSyncCodec, certificateId, createIdentifier, directMessageCodec, encryptMessage, messageTimelinePageCodec, type ResourceSyncStatus } from '@meshline/sdk';
import { GroupSecrets } from '../../../packages/sdk/dist/groups/secrets.js';
import { MessagingNetwork } from '../../support/messaging-network.js';
import { GroupNetwork } from '../../support/group-network.js';
import { ChannelNetwork } from '../../support/channel-network.js';
import { context } from '../../support/relay-fixture.js';

const resources: { dispose(): Promise<void> }[] = [];
afterEach(async () => { for (const resource of resources.splice(0).reverse()) await resource.dispose(); });
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
const empty = () => response({ items: [], certificates: [], has_more: false });
function signal() {
    let resolve!: () => void;
    const promise = new Promise<void>(done => { resolve = done; });
    return { promise, resolve };
}

test.each(['direct', 'group', 'channel'] as const)('active %s sync performs fresh passes without start and propagates errors', async kind => {
    let network: MessagingNetwork; let method: string;
    let synchronize: () => Promise<ResourceSyncStatus>; let status: () => Promise<ResourceSyncStatus>;
    if (kind === 'direct') {
        network = new MessagingNetwork(); resources.push(network); const a = await network.client(61);
        method = 'message.timeline.sync'; synchronize = () => a.messages.synchronize(network.descriptor.relayId); status = () => a.messages.getSyncStatus(network.descriptor.relayId);
        expect(a.messages.lifecycleState).toBe('stopped');
    } else if (kind === 'group') {
        const host = new GroupNetwork(); resources.push(host); network = host.network; const a = await host.client(61);
        const group = (await a.groups.createGroup(network.descriptor.relayId, { name: 'active', memberCapacity: 4 })).ref;
        method = 'group.sync'; synchronize = () => a.groups.synchronize(group); status = () => a.groups.getSyncStatus(group.groupId);
        expect(a.groups.lifecycleState).toBe('stopped');
    } else {
        const host = new ChannelNetwork(); resources.push(host); network = host.network; const a = await host.client(61);
        const channel = (await a.channels.createChannel(network.descriptor.relayId, 'active')).ref;
        method = 'channel.read'; synchronize = () => a.channels.synchronize(channel); status = () => a.channels.getSyncStatus(channel.channelId);
        expect(a.channels.lifecycleState).toBe('stopped');
    }
    const original = network.handleRequest; let reads = 0; let fail = false;
    network.handleRequest = async (name, ...args) => {
        if (name === method) { reads++; if (fail) return response({ code: 'forbidden', message: 'denied' }, 403); }
        return original?.(name, ...args);
    };
    expect((await synchronize()).state).toBe('caughtUp');
    const before = reads; expect((await synchronize()).state).toBe('caughtUp'); expect(reads).toBeGreaterThan(before);
    const completed = await status(); fail = true;
    await expect(synchronize()).rejects.toThrow();
    expect(await status()).toMatchObject({ state: 'blocked', blockReason: 'permission', lastSynchronizedAt: completed.lastSynchronizedAt });
    fail = false; expect((await synchronize()).state).toBe('caughtUp'); expect((await status()).error).toBeUndefined();
});

test('active message sync queues behind the background pass and canceled waiters never issue requests', async () => {
    const network = new MessagingNetwork(); resources.push(network); const a = await network.client(62); const relay = network.descriptor.relayId;
    const entered = signal(); const release = signal(); let requests = 0; let active = 0; let overlap = false;
    network.handleRequest = async method => {
        if (method !== 'message.timeline.sync') return undefined;
        if (++active > 1) overlap = true;
        try { if (++requests === 1) { entered.resolve(); await release.promise; } return empty(); }
        finally { active--; }
    };
    await a.messages.start();
    try {
        await entered.promise;
        const cancellation = new AbortController(); const canceled = a.messages.synchronize(relay, cancellation.signal); cancellation.abort();
        await expect(canceled).rejects.toBeDefined();
        const foreground = a.messages.synchronize(relay); expect(requests).toBe(1);
        release.resolve(); expect((await foreground).state).toBe('caughtUp'); expect(requests).toBe(2); expect(overlap).toBe(false);
    } finally { release.resolve(); await a.messages.stop(); }
});

test('cancellation preserves committed message pages and a new call resumes from the committed cursor', async () => {
    const network = new MessagingNetwork(); resources.push(network); const a = await network.client(63); const relay = network.descriptor.relayId;
    const message = await encryptMessage({ context, signer: a.device, messageId: createIdentifier('message'), createdAt: network.clock.wall,
        recipient: a.accountId, recipientDevices: [a.device.certificate], payload: directMessageCodec.encode({ body: { contentType: 'text/plain', text: 'committed' } }) });
    const afters: number[] = []; const cancellation = new AbortController();
    network.handleRequest = async (method, body) => {
        if (method !== 'message.timeline.sync') return undefined;
        const after = Number(body.after); afters.push(after);
        if (after < 0) return response(messageTimelinePageCodec.encode({ items: [{ sequence: 0, envelope: message.envelope, keyBox: message.recipientBoxes[0]!, acceptedAt: network.clock.wall }], certificates: [a.device.certificate], hasMore: true }));
        cancellation.abort(); return empty();
    };
    await expect(a.messages.synchronize(relay, cancellation.signal)).rejects.toBeDefined();
    await network.until(async () => (await a.messages.getSyncStatus(relay)).state === 'idle');
    expect((await a.messages.getMessage({ sender: a.accountId, messageId: message.envelope.messageId }))!.body!.text).toBe('committed');
    network.handleRequest = async (method, body) => { if (method !== 'message.timeline.sync') return undefined; afters.push(Number(body.after)); return empty(); };
    expect((await a.messages.synchronize(relay)).state).toBe('caughtUp'); expect(afters).toEqual([-1, 0, 0]);
});

test('manual group sync consumes downloaded private keys without starting either manager', async () => {
    const host = new GroupNetwork(); resources.push(host); const owner = await host.client(64);
    const group = (await owner.groups.createGroup(host.network.descriptor.relayId, { name: 'manual recovery', memberCapacity: 4 })).ref;
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'recoverable' } });
    const device = await host.client(64);
    expect((await device.groups.synchronize(group)).blockReason).toBe('missingKey');
    const secrets = new GroupSecrets({ store: owner.store, context, accountId: owner.accountId, deviceId: () => certificateId(owner.device.certificate, context), protector: owner.protector });
    const member = host.groups.get(group.groupId)!.state.members.find(value => value.account === owner.accountId)!;
    const privateKey = (await secrets.readMemberKey(group.groupId, member.memberEncryptionPublicKey))!;
    const encrypted = await encryptMessage({ context, signer: owner.device, messageId: createIdentifier('message'), createdAt: host.network.clock.wall,
        recipient: device.accountId, recipientDevices: [device.device.certificate], payload: accountGroupPrivateStateSyncCodec.encode({ states: [{ ...group, memberEncryptionPrivateKey: privateKey }] }) });
    privateKey.fill(0);
    const original = host.network.handleRequest!;
    host.network.handleRequest = async (method, body, ...args) => method === 'message.timeline.sync'
        ? response(messageTimelinePageCodec.encode({ items: Number(body.after) < 0 ? [{ sequence: 0, envelope: encrypted.envelope, keyBox: encrypted.recipientBoxes[0]!, acceptedAt: host.network.clock.wall }] : [], certificates: [owner.device.certificate], hasMore: false }))
        : original(method, body, ...args);
    await device.messages.synchronize(group.relayId);
    expect((await device.messages.readTimeline(0, 64)).some(value => value.payload['$type'] === 'meshline.account.group.state.sync')).toBe(true);
    const completed = await device.groups.synchronize(group);
    expect(completed.state).toBe('caughtUp'); expect(await device.groups.getSyncStatus(group.groupId)).toBe(completed);
    const reader = await device.groups.getMessages();
    try { expect((await reader.readNext(10)).map(value => value.body!.text)).toEqual(['recoverable']); } finally { await reader.dispose(); }
    expect(device.groups.lifecycleState).toBe('stopped'); expect(device.messages.lifecycleState).toBe('stopped');
});

test.each(['direct', 'group', 'channel'] as const)('stopping background %s work preserves manual completion, status and events', async kind => {
    let network: MessagingNetwork; let method: string;
    let synchronize: () => Promise<ResourceSyncStatus>; let status: () => Promise<ResourceSyncStatus>; let start: () => Promise<void>; let stop: () => Promise<void>;
    const changes: ResourceSyncStatus[] = [];
    if (kind === 'direct') {
        network = new MessagingNetwork(); resources.push(network); const a = await network.client(65); const manager = a.messages;
        method = 'message.timeline.sync'; synchronize = () => manager.synchronize(network.descriptor.relayId); status = () => manager.getSyncStatus(network.descriptor.relayId);
        start = () => manager.start(); stop = () => manager.stop(); manager.on('syncStatusChanged', value => { changes.push(value); });
    } else if (kind === 'group') {
        const host = new GroupNetwork(); resources.push(host); network = host.network; const a = await host.client(65); const manager = a.groups;
        const group = (await manager.createGroup(network.descriptor.relayId, { name: 'stop', memberCapacity: 4 })).ref;
        method = 'group.sync'; synchronize = () => manager.synchronize(group); status = () => manager.getSyncStatus(group.groupId);
        start = () => manager.start(); stop = () => manager.stop(); manager.on('syncStatusChanged', value => { changes.push(value); });
    } else {
        const host = new ChannelNetwork(); resources.push(host); network = host.network; const a = await host.client(65); const manager = a.channels;
        const channel = (await manager.createChannel(network.descriptor.relayId, 'stop')).ref; await manager.follow(channel);
        method = 'channel.read'; synchronize = () => manager.synchronize(channel); status = () => manager.getSyncStatus(channel.channelId);
        start = () => manager.start(); stop = () => manager.stop(); manager.on('syncStatusChanged', value => { changes.push(value); });
    }
    await start(); await network.until(async () => (await status()).state === 'caughtUp');
    const previous = await status(); const entered = signal(); const release = signal(); const original = network.handleRequest;
    const manual = new AsyncLocalStorage<boolean>();
    network.handleRequest = async (name, ...args) => { if (name === method && manual.getStore()) { entered.resolve(); await release.promise; } return original?.(name, ...args); };
    const foreground = manual.run(true, synchronize);
    try {
        await entered.promise; await stop();
        expect((await status()).state).toBe('synchronizing');
        network.clock.wall += 1; changes.length = 0; release.resolve();
        const completed = await foreground;
        expect(completed.state).toBe('caughtUp'); expect(completed.lastSynchronizedAt).toBeGreaterThan(previous.lastSynchronizedAt!);
        expect(await status()).toBe(completed); expect(changes).toEqual([completed]);
    } finally { release.resolve(); await foreground; await stop(); }
});
