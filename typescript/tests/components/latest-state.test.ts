import { afterEach, expect, test, vi } from 'vitest';
import { StateConflictError, StateUpdateSupersededError, type JsonObject, type RelayClient } from '@meshline/sdk';
import { GroupNetwork } from '../support/group-network.js';
import { ChannelNetwork } from '../support/channel-network.js';

function deferred() { let resolve!: () => void; const promise = new Promise<void>(done => { resolve = done; }); return { promise, resolve }; }
const networks: { dispose(): Promise<void> }[] = [];
afterEach(async () => { for (const network of networks.splice(0).reverse()) await network.dispose(); });

async function fixture(kind: 'group' | 'channel') {
    if (kind === 'group') {
        const network = new GroupNetwork(); networks.push(network); const owner = await network.client(51);
        const created = await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'original', memberCapacity: 10 });
        return { owner, network: network.network, method: 'group.update', collection: 'group_operations', resource: created.ref.groupId,
            update: (patch: { name?: string; description?: string | null }) => owner.groups.updateGroup(created.ref, patch).then(info => info.group),
            current: async () => (await owner.groups.getGroup(created.ref)).group,
            resume: async () => { const resumed = await network.client(51, owner.path); return { manager: resumed.groups, current: async () => (await resumed.groups.getGroup(created.ref)).group, store: resumed.store }; },
            manager: owner.groups };
    }
    const network = new ChannelNetwork(); networks.push(network); const owner = await network.client(31);
    const created = await owner.channels.createChannel(network.network.descriptor.relayId, 'original');
    return { owner, network: network.network, method: 'channel.update', collection: 'channel_operations', resource: created.ref.channelId,
        update: (patch: { name?: string; description?: string | null }) => owner.channels.updateChannel(created.ref, patch).then(info => info.descriptor!),
        current: async () => (await owner.channels.getChannel(created.ref)).descriptor!,
        resume: async () => { const resumed = await network.client(31, owner.path); return { manager: resumed.channels, current: async () => (await resumed.channels.getChannel(created.ref)).descriptor!, store: resumed.store }; },
        manager: owner.channels };
}

test.each(['group', 'channel'] as const)('%s updates merge during blocked signing and invalid calls do not supersede', async kind => {
    const f = await fixture(kind); const entered = deferred(); const release = deferred(); const original = f.owner.device.sign.bind(f.owner.device);
    let held = false; vi.spyOn(f.owner.device, 'sign').mockImplementation(async (input, signal) => {
        if (!held) { held = true; entered.resolve(); await release.promise; }
        return original(input, signal);
    });
    const first = f.update({ name: 'old', description: 'retained' }); const superseded = expect(first).rejects.toBeInstanceOf(StateUpdateSupersededError);
    try {
        await entered.promise;
        await expect(Promise.resolve().then(async () => await f.update({ name: '' }))).rejects.toThrow();
        expect((await f.owner.store.read([{ collection: f.collection }])).sets[0]![0]!.value.generation).toBe(1);
        expect(await f.update({ name: 'latest' })).toMatchObject({ name: 'latest', description: 'retained' }); await superseded;
        release.resolve(); await f.manager.dispose();
        const row = (await f.owner.store.read([{ collection: f.collection }])).sets[0]![0]!.value;
        expect(row).toMatchObject({ generation: 2, pending: false, intent: null });
    } finally { release.resolve(); }
});

test.each([
    ['group', 'before'], ['group', 'after'], ['channel', 'before'], ['channel', 'after'],
] as const)('%s keeps the latest state when the old request is delayed %s relay acceptance', async (kind, stage) => {
    const f = await fixture(kind); const entered = deferred(); const release = deferred(); const finished = deferred();
    const original = f.network.handleRequest!; let held = false;
    f.network.handleRequest = async (...args) => {
        if (args[0] !== f.method || held) return original(...args);
        held = true;
        if (stage === 'before') { entered.resolve(); await release.promise; }
        try {
            const response = await original(...args);
            if (stage === 'after') { entered.resolve(); await release.promise; }
            return response;
        } finally { finished.resolve(); }
    };
    const first = f.update({ name: 'old', description: 'retained' }); const superseded = expect(first).rejects.toBeInstanceOf(StateUpdateSupersededError);
    try {
        await entered.promise; await f.update({ name: 'latest' }); await superseded;
        release.resolve(); await finished.promise;
        expect(await f.current()).toMatchObject({ name: 'latest', description: 'retained' });
        expect(await f.update({ description: null })).toMatchObject({ name: 'latest' });
        expect((await f.current()).description).toBeUndefined();
    } finally { release.resolve(); }
});

test.each(['group', 'channel'] as const)('%s resumes only the latest merged update after timeouts and restart', async kind => {
    const f = await fixture(kind); const client = f.owner.pool.clients.find(client => client.sessionMode === 'device')!;
    const original = client.requestHttp.bind(client); const sent: JsonObject[] = [];
    vi.spyOn(client, 'requestHttp').mockImplementation(async (...args: Parameters<RelayClient['requestHttp']>) => {
        if (args[1] === f.method) { sent.push(args[2]!); throw new TypeError('Response timeout'); }
        return original(...args);
    });
    await expect(f.update({ name: 'old', description: 'retained' })).rejects.toThrow('timeout');
    await expect(f.update({ name: 'latest' })).rejects.toThrow('timeout'); expect(sent).toHaveLength(2);
    await f.owner.dispose(); const resumed = await f.resume(); f.network.clock.tick(); await resumed.manager.start();
    await f.network.until(async () => (await resumed.store.read([{ collection: f.collection }])).sets[0]!.every(row => row.value.pending === false));
    await resumed.manager.stop(); expect(await resumed.current()).toMatchObject({ name: 'latest', description: 'retained' });
});

test.each(['group', 'channel'] as const)('%s observers can dispose after a committed update without changing its result', async kind => {
    if (kind === 'group') {
        const network = new GroupNetwork(); networks.push(network); const owner = await network.client(51);
        const group = await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'original', memberCapacity: 10 });
        let disposal: Promise<void> | undefined;
        owner.groups.on('groupChanged', () => { disposal = owner.groups.dispose(); });
        expect((await owner.groups.updateGroup(group.ref, { name: 'accepted' })).group.name).toBe('accepted');
        await disposal;
    } else {
        const network = new ChannelNetwork(); networks.push(network); const owner = await network.client(31);
        const channel = await owner.channels.createChannel(network.network.descriptor.relayId, 'original');
        let disposal: Promise<void> | undefined;
        owner.channels.on('channelChanged', () => { disposal = owner.channels.dispose(); });
        expect((await owner.channels.updateChannel(channel.ref, { name: 'accepted' })).descriptor!.name).toBe('accepted');
        await disposal;
    }
});

test('channel recovery recognizes an accepted revision without overwriting a later update from another device', async () => {
    const network = new ChannelNetwork(); networks.push(network); const owner = await network.client(31);
    const channel = await owner.channels.createChannel(network.network.descriptor.relayId, 'original');
    network.loseResponse = 'channel.update';
    await expect(owner.channels.updateChannel(channel.ref, { name: 'accepted first' })).rejects.toThrow('response lost');
    network.loseResponse = undefined;
    const anotherDevice = await network.client(31);
    await anotherDevice.channels.updateChannel(channel.ref, { name: 'later from another device' });
    await owner.dispose();
    const resumed = await network.client(31, owner.path); network.network.clock.tick(); await resumed.channels.start();
    await network.network.until(async () => (await resumed.store.read([{ collection: 'channel_operations' }])).sets[0]!.every(row => row.value.pending === false));
    await resumed.channels.stop();
    expect((await resumed.channels.getChannel(channel.ref)).descriptor).toMatchObject({ name: 'later from another device', revision: 2 });
    expect(network.requests.filter(request => request.method === 'channel.update')).toHaveLength(2);
});

test('group signing contention yields after bounded retries and preserves the update for recovery', async () => {
    const network = new GroupNetwork(); networks.push(network); const owner = await network.client(51);
    const group = await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'original', memberCapacity: 10 });
    const anotherDevice = await network.client(51); const original = owner.device.sign.bind(owner.device);
    let changes = 0;
    const signing = vi.spyOn(owner.device, 'sign').mockImplementation(async (input, signal) => {
        if (changes < 3) {
            await anotherDevice.groups.updateGroup(group.ref, { description: `concurrent ${++changes}` });
            await owner.groups.getGroup(group.ref);
        }
        return original(input, signal);
    });
    await expect(owner.groups.updateGroup(group.ref, { name: 'retained' })).rejects.toBeInstanceOf(StateConflictError);
    expect(signing).toHaveBeenCalledTimes(3); signing.mockRestore();
    expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value).toMatchObject({ pending: true, intent: { name: 'retained' } });
    await owner.dispose();
    const resumed = await network.client(51, owner.path); network.network.clock.tick(); await resumed.groups.start();
    await network.network.until(async () => (await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.every(row => row.value.pending === false));
    await resumed.groups.stop();
    expect((await resumed.groups.getGroup(group.ref)).group).toMatchObject({ name: 'retained', description: 'concurrent 3' });
});

test.each(['group', 'channel'] as const)('%s recovery confirms an accepted update even if the resource has since closed', async kind => {
    if (kind === 'group') {
        const network = new GroupNetwork(); networks.push(network); const owner = await network.client(51);
        const group = await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'original', memberCapacity: 10 });
        network.loseResponse = 'group.update';
        await expect(owner.groups.updateGroup(group.ref, { name: 'accepted' })).rejects.toThrow('response lost');
        network.loseResponse = undefined; await owner.groups.closeGroup(group.ref); await owner.dispose();
        const resumed = await network.client(51, owner.path); network.network.clock.tick(); await resumed.groups.start();
        await network.network.until(async () => (await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.every(row => row.value.pending === false));
        await resumed.groups.stop();
        expect((await resumed.groups.getGroup(group.ref)).group).toMatchObject({ name: 'accepted', status: 'closed' });
        expect(network.requests.filter(request => request.method === 'group.update')).toHaveLength(1);
    } else {
        const network = new ChannelNetwork(); networks.push(network); const owner = await network.client(31);
        const channel = await owner.channels.createChannel(network.network.descriptor.relayId, 'original');
        network.loseResponse = 'channel.update';
        await expect(owner.channels.updateChannel(channel.ref, { name: 'accepted' })).rejects.toThrow('response lost');
        network.loseResponse = undefined; await owner.channels.closeChannel(channel.ref); await owner.dispose();
        const resumed = await network.client(31, owner.path); network.network.clock.tick(); await resumed.channels.start();
        await network.network.until(async () => (await resumed.store.read([{ collection: 'channel_operations' }])).sets[0]!.every(row => row.value.pending === false));
        await resumed.channels.stop();
        expect((await resumed.channels.getChannel(channel.ref)).descriptor).toMatchObject({ name: 'accepted', status: 'closed' });
        expect(network.requests.filter(request => request.method === 'channel.update')).toHaveLength(1);
    }
});
