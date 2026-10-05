import { afterEach, expect, test, vi } from 'vitest';
import type { HistoryRange, QueryReader } from '@meshline/sdk';
import { MessagingNetwork } from '../../support/messaging-network.js';
import { GroupNetwork } from '../../support/group-network.js';
import { ChannelNetwork } from '../../support/channel-network.js';

const resources: { dispose(): Promise<void> }[] = [];
afterEach(async () => { for (const resource of resources.splice(0).reverse()) await resource.dispose(); });

const kinds = ['direct', 'group', 'channel'] as const;
async function fixture(kind: typeof kinds[number]) {
    let open: (bounds?: HistoryRange | null, signal?: AbortSignal) => Promise<QueryReader<{ readonly localSequence: number }>>;
    let unfiltered: () => Promise<QueryReader<{ readonly localSequence: number }>>;
    let legacy: (signal?: AbortSignal) => Promise<QueryReader<{ readonly localSequence: number }>>;
    let unscoped!: (bounds: HistoryRange) => Promise<QueryReader<{ readonly localSequence: number }>>;
    let reopen: () => Promise<void>; let append: () => Promise<unknown>;
    if (kind === 'direct') {
        const network = new MessagingNetwork(); resources.push(network); let owner = await network.client(71);
        append = () => owner.messages.sendMessage(owner.accountId, { body: { contentType: 'text/plain', text: 'item' } });
        for (let i = 0; i < 5; i++) await append();
        open = (bounds, signal) => owner.messages.getMessageHistory(owner.accountId, bounds, signal);
        unfiltered = () => owner.messages.getMessageHistory();
        legacy = signal => owner.messages.getMessageHistory(owner.accountId, signal);
        reopen = async () => { await owner.dispose(); owner = await network.client(71, owner.path); };
    } else if (kind === 'group') {
        const host = new GroupNetwork(); resources.push(host); let owner = await host.client(71);
        const group = (await owner.groups.createGroup(host.network.descriptor.relayId, { name: 'history', memberCapacity: 2 })).ref;
        append = () => owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'item' } });
        for (let i = 0; i < 5; i++) await append();
        open = (bounds, signal) => owner.groups.getMessages({ groupId: group.groupId, sender: owner.accountId }, bounds, signal);
        reopen = async () => { await owner.dispose(); owner = await host.client(71, owner.path); };
        unfiltered = () => owner.groups.getMessages();
        legacy = signal => owner.groups.getMessages({ groupId: group.groupId, sender: owner.accountId }, signal);
        unscoped = bounds => owner.groups.getMessages({}, bounds);
    } else {
        const host = new ChannelNetwork(); resources.push(host); let owner = await host.client(71);
        const channel = (await owner.channels.createChannel(host.network.descriptor.relayId, 'history')).ref;
        for (let i = 0; i < 6; i++) {
            const post = await owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: `item-${i}` } });
            if (i === 3) await owner.channels.deletePost(post.ref);
        }
        append = () => owner.channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'new' } });
        open = (bounds, signal) => owner.channels.getPosts({ channelId: channel.channelId, author: owner.accountId }, bounds, signal);
        reopen = async () => { await owner.dispose(); owner = await host.client(71, owner.path); };
        unfiltered = () => owner.channels.getPosts();
        legacy = signal => owner.channels.getPosts({ channelId: channel.channelId, author: owner.accountId }, signal);
        unscoped = bounds => owner.channels.getPosts({}, bounds);
    }
    async function read(bounds: HistoryRange | null | undefined, count = 2, signal?: AbortSignal): Promise<number[]> {
        const reader = await open(bounds, signal);
        try { return (await reader.readNext(count, signal)).map(value => value.localSequence); } finally { await reader.dispose(); }
    }
    return { open, read, unfiltered, legacy, reopen, append, unscoped };
}

test.each(kinds)('%s history preserves default, null and legacy query signatures', async kind => {
    const f = await fixture(kind);
    const all = await f.read({}, 50);
    expect(all).toHaveLength(5);
    expect(await f.read(undefined, 50)).toEqual(all);
    expect(await f.read(null, 50)).toEqual(all);
    for (const open of [f.unfiltered, f.legacy]) {
        const reader = await open();
        try { expect((await reader.readNext(50)).map(value => value.localSequence)).toEqual(all); }
        finally { await reader.dispose(); }
    }
});

test.each(kinds)('%s history bounds are exclusive and each batch is ascending', async kind => {
    const f = await fixture(kind);
    const all = await f.read({}, 50);
    const bounded = await f.read({ after: all[0]!, before: all[4]! });
    expect(bounded).toEqual(all.slice(2, 4));
    expect(await f.read({ after: all[0]! })).toEqual(all.slice(1, 3));
    expect(await f.read({ after: all[0]!, before: bounded[0]! })).toEqual(all.slice(1, 2));
    expect(await f.read({ after: Number.MAX_SAFE_INTEGER })).toEqual([]);
});

test.each(kinds)('%s history captures the range before caller mutation', async kind => {
    const f = await fixture(kind);
    const all = await f.read({}, 50);
    const range = { before: all.at(-1)! + 1 };
    const opening = f.open(range);
    range.before = 0;
    const reader = await opening;
    try { expect((await reader.readNext(2)).map(value => value.localSequence)).toEqual(all.slice(-2)); }
    finally { await reader.dispose(); }
});

test.each(kinds)('%s history snapshots exclude later writes and canceled reads preserve position', async kind => {
    const f = await fixture(kind);
    const all = await f.read({}, 50);
    const snapshot = await f.open({ before: all.at(-1)! + 1 });
    try {
        const controller = new AbortController();
        controller.abort(new Error('canceled history'));
        await expect(snapshot.readNext(0)).rejects.toThrow();
        await expect(snapshot.readNext(2, controller.signal)).rejects.toThrow('canceled history');
        expect((await snapshot.readNext(2)).map(value => value.localSequence)).toEqual(all.slice(-2));
        await f.append();
        expect((await snapshot.readNext(50)).map(value => value.localSequence)).toEqual(all.slice(0, 3));
        expect(await snapshot.readNext(1)).toEqual([]);
    } finally { await snapshot.dispose(); }
    await expect(snapshot.readNext(1)).rejects.toMatchObject({ code: 'disposed' });
});

test.each(kinds)('%s page boundaries continue across restart and new queries see later writes', async kind => {
    const f = await fixture(kind);
    const all = await f.read({}, 50);
    const first = await f.read({ before: all.at(-1)! + 1 });
    expect(first).toEqual(all.slice(-2));
    await f.append();
    await f.reopen();
    const second = await f.read({ before: first[0]! });
    expect(second).toEqual(all.slice(1, 3));
    expect(await f.read({ before: second[0]! })).toEqual(all.slice(0, 1));
    expect(await f.read({ after: all.at(-1)! })).toHaveLength(1);
});

test.each(kinds)('%s history opening honors cancellation with range and legacy arguments', async kind => {
    const f = await fixture(kind);
    const controller = new AbortController();
    controller.abort(new Error('canceled history'));
    await expect(f.read({}, 2, controller.signal)).rejects.toThrow('canceled history');
    await expect(f.read(null, 2, controller.signal)).rejects.toThrow('canceled history');
    await expect(Promise.resolve().then(() => f.legacy(controller.signal))).rejects.toThrow('canceled history');
});

const invalidRanges = [{ after: -1 }, { after: 3, before: 3 }, { after: 4, before: 3 }, { before: -1 }, { after: Number.MAX_SAFE_INTEGER + 1 }];
test.each(kinds.flatMap(kind => invalidRanges.map(range => ({ kind, range }))))('$kind rejects invalid range $range', async ({ kind, range }) => {
    const f = await fixture(kind);
    await expect(f.read(range)).rejects.toThrow();
});

test.each(['group', 'channel'] as const)('%s sequence bounds require a resource ID', async kind => {
    const f = await fixture(kind);
    expect(() => f.unscoped(kind === 'group' ? { after: 1 } : { before: 100 })).toThrow(kind === 'group' ? 'groupId' : 'channelId');
});

test('indexed direct readers bound payload reads and retry canceled projection without skipping', async () => {
    const network = new MessagingNetwork(); resources.push(network); const owner = await network.client(72);
    for (let i = 0; i < 6; i++) await owner.messages.sendMessage(owner.accountId, { body: { contentType: 'text/plain', text: `item-${i}` } });
    await (await owner.messages.getMessageHistory()).dispose(); // Existing stores are indexed once.
    const underlying = owner.store.read.bind(owner.store);
    const reads = vi.spyOn(owner.store, 'read'); const queries = vi.spyOn(owner.store, 'openQuery');
    const reader = await owner.messages.getMessageHistory(owner.accountId, { before: 6 });
    try {
        const controller = new AbortController();
        reads.mockImplementationOnce(async (query, signal) => {
            const rows = await underlying(query, signal); controller.abort(new Error('projection canceled')); return rows;
        });
        await expect(reader.readNext(2, controller.signal)).rejects.toThrow('projection canceled');
        reads.mockRestore(); const subsequent = vi.spyOn(owner.store, 'read');
        const concurrent = await Promise.all([reader.readNext(2), reader.readNext(2)]);
        expect(concurrent.map(batch => batch.map(value => value.localSequence))).toEqual([[4, 5], [2, 3]]);
        expect(subsequent.mock.calls.flatMap(([query]) => query).filter(query => query.collection === 'messages')).toHaveLength(2);
        expect(subsequent.mock.calls.flatMap(([query]) => query).filter(query => query.collection === 'messages').every(query => query.key !== undefined)).toBe(true);
        expect(queries.mock.calls[0]![0]).toMatchObject({ collection: 'message_history', reverse: true, before: `${owner.accountId}|0000000000000006` });
    } finally { await reader.dispose(); }
    await owner.messages.sendMessage(owner.accountId, { body: { contentType: 'text/plain', text: 'new' } });
    const newest = await owner.messages.getMessageHistory(undefined, { after: 6 });
    try { expect((await newest.readNext(1))[0]!.localSequence).toBe(7); } finally { await newest.dispose(); }
});
