import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test, vi } from 'vitest';
import { createIdentifier, encryptMessage, messageSendRequestCodec, RelayError, StateConflictError, type MessageDeliveryStatus, type MeshlineStore, type StoreMutation } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
// Load the built internal engine so adapters and errors share the same package instance.
import { MessageOutbox, encodeOutbox, outboxKey, type OutboxRecord, type OutboxEffects, type OutboxTransport } from '../../packages/sdk/dist/messages/outbox.js';
import { messageDevice } from '../support/message-fixture.js';
import { context } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { removeTestDirectory } from '../support/temp.js';

const directories: string[] = []; const stores: MeshlineStore[] = [];
afterEach(async () => { for (const store of stores.splice(0)) await store.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
const from = messageDevice(context, 1, 2); const to = messageDevice(context, 3, 4);
const relayA = `0x${'1'.repeat(40)}`; const relayB = `0x${'2'.repeat(40)}`; const id = 'msg_AAECAwQFBgcICQoLDA0ODw';
async function fixture() {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-outbox-')); directories.push(directory); const path = join(directory, 'state.sqlite');
    const clock = new AdvancingClock(); const store = new NodeSqliteStore(path); stores.push(store); await store.migrate(); await store.initialize({ context: context.toString(), accountId: from.certificate.account });
    const request = await encryptMessage({ context, signer: from, messageId: id, createdAt: clock.wall, recipient: to.certificate.account, recipientDevices: [to.certificate], payload: { $type: 'future.message', value: 'secret' } });
    const record: OutboxRecord = { request, relayId: relayA, state: 'queued', nextAttemptAt: clock.wall, isDirect: true };
    let home = relayA; let reply: MessageDeliveryStatus = { status: 'delivering', acceptedAt: clock.wall }; const calls: { relay: string; kind: string; json?: string }[] = [];
    const transport: OutboxTransport = { getHome: async () => home, currentHome: () => home,
        prepare: async relay => ({ send: async request => { calls.push({ relay, kind: 'send', json: messageSendRequestCodec.stringify(request) }); return reply; }, status: async () => { calls.push({ relay, kind: 'status' }); return reply; } }) };
    async function save(value: OutboxRecord = record) { const snapshot = await store.read([]); await store.commit(snapshot.version, [{ kind: 'put', ...outboxKey(id), value: encodeOutbox(value) }]); }
    const queue = new MessageOutbox(store, clock, transport); await save();
    return { store, path, clock, request, record, transport, calls, queue, save, moveHome() { home = relayB; }, reply(value: MessageDeliveryStatus) { reply = value; } };
}

test('submission is durable before I/O; accepted messages only poll the accepting relay and retain target acceptance', async () => {
    const f = await fixture(); const prepare = f.transport.prepare;
    f.transport.prepare = async (relay, signal) => { const client = await prepare(relay, signal); return { ...client, send: async (request, token) => {
        expect((await f.queue.get(id))!.state).toBe('submitting'); expect(await f.queue.cancel(id)).toBeUndefined(); return client.send(request, token);
    } }; };
    expect((await f.queue.process(id)).changes.map(value => value.current.state)).toEqual(['submitting', 'relayAccepted']);
    expect((await f.queue.get(id))!.acceptedAt).toBe(f.clock.wall);
    f.moveHome(); f.clock.wall += 15; f.reply({ status: 'target_accepted', acceptedAt: f.clock.wall - 15 });
    const result = await f.queue.process(id); expect(result.changes.at(-1)!.current.state).toBe('targetAccepted'); expect((await f.queue.get(id))!.state).toBe('targetAccepted');
    expect(await f.queue.due()).toEqual([]); expect(await f.queue.cancel(id)).toBeUndefined();
    f.clock.wall += 60; expect((await f.queue.process(id)).changes).toEqual([]);
    await f.store.dispose(); const reopened = new NodeSqliteStore(f.path); stores.push(reopened); await reopened.initialize({ context: context.toString(), accountId: from.certificate.account });
    const resumed = new MessageOutbox(reopened, f.clock, f.transport); await resumed.recover();
    expect((await resumed.get(id))!.state).toBe('targetAccepted'); expect((await resumed.get(id))!.acceptedAt).toBe(f.clock.wall - 75);
    expect(f.calls.map(value => [value.relay, value.kind])).toEqual([[relayA, 'send'], [relayA, 'status']]);
});

test('uncertain responses persist the exact original request across database reopen and retry', async () => {
    const f = await fixture(); const original = messageSendRequestCodec.stringify(f.request); const prepare = f.transport.prepare;
    f.transport.prepare = async (relay, signal) => { const client = await prepare(relay, signal); return { ...client, send: async (request, token) => { await client.send(request, token); throw new Error('response lost after acceptance'); } }; };
    const first = await f.queue.process(id); expect(first.error).toBeInstanceOf(Error); expect((await f.queue.get(id))!.state).toBe('submissionUnknown');
    await f.store.dispose(); const reopened = new NodeSqliteStore(f.path); stores.push(reopened); await reopened.initialize({ context: context.toString(), accountId: from.certificate.account });
    const queue = new MessageOutbox(reopened, f.clock, { ...f.transport, prepare }); await queue.recover(); f.clock.wall += 15;
    await queue.process(id); expect(f.calls.map(value => value.json)).toEqual([original, original]); expect((await queue.get(id))!.state).toBe('relayAccepted');
});

test('crash recovery converts submitting to unknown and never enables cancellation', async () => {
    const f = await fixture(); await f.save({ ...f.record, state: 'submitting' });
    expect((await f.queue.recover()).map(value => value.current.state)).toEqual(['submissionUnknown']);
    expect(await f.queue.cancel(id)).toBeUndefined(); expect(await f.queue.recover()).toEqual([]);
});

test('home migration leaves uncertain messages at their original relay even when status is not found', async () => {
    const f = await fixture(); await f.save({ ...f.record, state: 'submissionUnknown' }); f.moveHome();
    f.transport.prepare = async relay => ({ send: async () => { throw new Error('must not send'); }, status: async () => { f.calls.push({ relay, kind: 'status' }); throw new RelayError({ code: 'not_found', message: 'unknown at old relay' }); } });
    expect((await f.queue.process(id)).error).toBeInstanceOf(RelayError); const record = (await f.queue.get(id))!;
    expect(record.state).toBe('submissionUnknown'); expect(record.relayId).toBe(relayA); expect(f.calls).toEqual([{ relay: relayA, kind: 'status' }]);
    expect(record.errorMessage).toBe('unknown at old relay');
});

test('only a definitive rejection of the first submission is terminal', async () => {
    const f = await fixture();
    f.transport.prepare = async () => ({ send: async () => { throw new RelayError({ code: 'device_unknown', message: 'revoked' }); }, status: async () => { throw new Error('unexpected'); } });
    await f.queue.process(id); expect((await f.queue.get(id))!.state).toBe('failed');
    await f.save({ ...f.record, state: 'submissionUnknown' }); await f.queue.process(id); expect((await f.queue.get(id))!.state).toBe('submissionUnknown');
});

test('preflight failures and home changes keep unsent messages queued', async () => {
    const f = await fixture(); f.transport.getHome = async () => { throw new Error('registry unavailable'); };
    await f.queue.process(id); expect((await f.queue.get(id))!.state).toBe('queued'); expect(f.calls).toEqual([]);
    await f.save(); f.transport.getHome = async () => relayA; const prepare = f.transport.prepare;
    f.transport.prepare = async (relay, signal) => { f.moveHome(); return prepare(relay, signal); };
    await f.queue.process(id); expect((await f.queue.get(id))!.state).toBe('queued'); expect(f.calls).toEqual([]);
});

test('queued messages follow the new home without changing the immutable request', async () => {
    const f = await fixture(); f.moveHome(); await f.queue.process(id);
    expect(f.calls[0]).toEqual({ relay: relayB, kind: 'send', json: messageSendRequestCodec.stringify(f.request) }); expect((await f.queue.get(id))!.relayId).toBe(relayB);
});

test('cancellation during preparation wins the CAS claim; cancellation after I/O begins becomes unknown', async () => {
    const f = await fixture(); const prepare = f.transport.prepare;
    f.transport.prepare = async (relay, signal) => { expect((await f.queue.cancel(id))!.current.state).toBe('canceled'); return prepare(relay, signal); };
    await f.queue.process(id); expect(f.calls).toEqual([]); expect((await f.queue.get(id))!.state).toBe('canceled');
    await f.save(); const controller = new AbortController();
    f.transport.prepare = async () => ({ send: async () => { controller.abort(); throw controller.signal.reason; }, status: async () => { throw new Error('unexpected'); } });
    const result = await f.queue.process(id, controller.signal); expect(result.error).toBe(controller.signal.reason); expect((await f.queue.get(id))!.state).toBe('submissionUnknown');
});

test('malformed results and changed acceptance time retain previous evidence and report the error', async () => {
    const f = await fixture(); f.reply({ status: 'failed', acceptedAt: f.clock.wall });
    expect((await f.queue.process(id)).error).toBeInstanceOf(Error); expect((await f.queue.get(id))!.state).toBe('submissionUnknown');
    await f.save({ ...f.record, state: 'relayAccepted', acceptedAt: f.clock.wall }); f.reply({ status: 'target_accepted', acceptedAt: f.clock.wall + 1 });
    expect((await f.queue.process(id)).error).toBeInstanceOf(Error); expect((await f.queue.get(id))!.state).toBe('relayAccepted'); expect((await f.queue.get(id))!.acceptedAt).toBe(f.clock.wall);
});

test('local persistence failure after target acceptance cannot silently discard required delivery effects', async () => {
    const f = await fixture(); let fail = true; f.reply({ status: 'target_accepted', acceptedAt: f.clock.wall });
    const effects: OutboxEffects = { queries: [{ collection: 'test_contacts', key: 'peer' }], plan(_snapshot, change) {
        if (change.current.state !== 'targetAccepted') return [];
        if (fail) throw new Error('contact evidence transaction failed');
        return [{ kind: 'put', collection: 'test_contacts', key: 'peer', value: { confirmed: true } }];
    } };
    const queue = new MessageOutbox(f.store, f.clock, f.transport, effects); expect((await queue.process(id)).error).toBeInstanceOf(Error);
    expect((await queue.get(id))!.state).toBe('relayAccepted'); expect((await f.store.read([{ collection: 'test_contacts' }])).sets[0]).toEqual([]);
    fail = false; f.clock.wall += 15; await queue.process(id); expect((await queue.get(id))!.state).toBe('targetAccepted');
    expect((await f.store.read([{ collection: 'test_contacts' }])).sets[0]![0]!.value).toEqual({ confirmed: true });
    expect(f.calls.map(value => value.kind)).toEqual(['send', 'status']);
});

test('unrelated CAS writes retry locally without replaying the network request', async () => {
    const f = await fixture(); const commit = f.store.commit.bind(f.store); let conflict = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (conflict && mutations.some(value => value.kind === 'put' && value.value.state === 'relayAccepted')) { conflict = false;
            await commit(version, [{ kind: 'put', collection: 'unrelated', key: 'row', value: { value: 1 } }]); throw new StateConflictError('concurrent local write'); }
        return commit(version, mutations, signal);
    });
    await f.queue.process(id); expect(f.calls).toHaveLength(1); expect((await f.queue.get(id))!.state).toBe('relayAccepted');
});

test('another queue completing the request cannot be overwritten by a late older attempt', async () => {
    const f = await fixture(); let release: (() => void) | undefined; const gate = new Promise<void>(resolve => { release = resolve; });
    let enter: (() => void) | undefined; const entered = new Promise<void>(resolve => { enter = resolve; });
    f.transport.prepare = async () => ({ send: async () => { enter!(); await gate; throw new Error('late response lost'); }, status: async () => { throw new Error('unexpected'); } });
    const first = f.queue.process(id); await entered;
    const second = new MessageOutbox(f.store, f.clock, { ...f.transport, prepare: async () => ({ send: async () => ({ status: 'target_accepted', acceptedAt: f.clock.wall }), status: async () => { throw new Error('unexpected'); } }) });
    await second.recover(); await second.process(id); release!(); await first; expect((await f.queue.get(id))!.state).toBe('targetAccepted');
});

test('due work has stable chronological ordering, skips terminal entries and respects retry time', async () => {
    const f = await fixture(); const otherId = 'msg_EBESExQVFhcYGRobHB0eHw'; const other: OutboxRecord = { ...f.record, state: 'submissionUnknown', request: { ...f.request, envelope: { ...f.request.envelope, messageId: otherId, createdAt: f.clock.wall - 1 } } };
    const snapshot = await f.store.read([]); const mutations: StoreMutation[] = [{ kind: 'put', ...outboxKey(otherId), value: encodeOutbox(other) }]; await f.store.commit(snapshot.version, mutations);
    expect(await f.queue.due()).toEqual([otherId, id]); await f.queue.cancel(id); expect(await f.queue.due()).toEqual([otherId]);
    await f.save({ ...f.record, nextAttemptAt: f.clock.wall + 15 }); expect(await f.queue.due()).toEqual([otherId]);
});

test('recovery keeps 1000 terminal records with stable tie ordering and preserves all pending states', async () => {
    const f = await fixture(); const failedId = 'msg_EBESExQVFhcYGRobHB0eHw'; const canceledId = 'msg_ICEiIyQlJicoKSorLC0uLw';
    const record = (messageId: string, state: OutboxRecord['state'], offset: number): OutboxRecord => ({ ...f.record, state,
        request: { ...f.request, envelope: { ...f.request.envelope, messageId, createdAt: f.clock.wall + offset } },
        ...(['relayAccepted', 'targetAccepted'].includes(state) ? { acceptedAt: f.clock.wall } : {}) });
    const pending = (['queued', 'submitting', 'submissionUnknown', 'relayAccepted'] as const).map(state => record(createIdentifier('message'), state, -100));
    const records = [record(id, 'targetAccepted', 0), record(failedId, 'failed', 10), record(canceledId, 'canceled', 10), ...pending,
        ...Array.from({ length: 999 }, (_, index) => ({ ...record(createIdentifier('message'), 'canceled', 100), isDirect: index !== 0 }))];
    const snapshot = await f.store.read([]); await f.store.commit(snapshot.version, records.map(value => ({ kind: 'put', ...outboxKey(value.request.envelope.messageId), value: encodeOutbox(value) })));
    await f.queue.recover();
    expect(await f.queue.get(id)).toBeUndefined(); expect(await f.queue.get(failedId)).toBeUndefined(); expect((await f.queue.get(canceledId))!.state).toBe('canceled');
    for (const value of pending) expect((await f.queue.get(value.request.envelope.messageId))!.state).toBe(value.state === 'submitting' ? 'submissionUnknown' : value.state);
    expect(await f.queue.due()).toHaveLength(4); expect((await f.store.read([{ collection: 'message_outbox' }])).sets[0]).toHaveLength(1004);
    expect(await f.queue.get(createIdentifier('message'))).toBeUndefined();
});

test('late completion and concurrent terminal writes enforce the shared bound without replaying a send', async () => {
    const f = await fixture(); const oldest = createIdentifier('message'); const recent = createIdentifier('message'); const concurrent = createIdentifier('message');
    const record = (messageId: string, state: OutboxRecord['state'], offset: number): OutboxRecord => ({ ...f.record, state,
        request: { ...f.request, envelope: { ...f.request.envelope, messageId, createdAt: f.clock.wall + offset } },
        ...(state === 'targetAccepted' ? { acceptedAt: f.clock.wall } : {}) });
    const snapshot = await f.store.read([]); await f.store.commit(snapshot.version, [record(id, 'queued', -100), record(oldest, 'targetAccepted', -10), record(recent, 'queued', 200),
        ...Array.from({ length: 999 }, () => record(createIdentifier('message'), 'canceled', 0))]
        .map(value => ({ kind: 'put', ...outboxKey(value.request.envelope.messageId), value: encodeOutbox(value) })));
    expect((await f.queue.cancel(id))!.current.state).toBe('canceled'); expect(await f.queue.get(id)).toBeUndefined(); expect(await f.queue.get(oldest)).toBeDefined();
    const commit = f.store.commit.bind(f.store); let inject = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (inject && mutations.some(value => value.kind === 'put' && value.key === recent && value.value.state === 'targetAccepted')) {
            inject = false; await commit(version, [{ kind: 'put', ...outboxKey(concurrent), value: encodeOutbox(record(concurrent, 'targetAccepted', 300)) }]);
        }
        return commit(version, mutations, signal);
    });
    f.reply({ status: 'target_accepted', acceptedAt: f.clock.wall }); await f.queue.process(recent);
    expect(inject).toBe(false); expect(f.calls).toHaveLength(1); expect(await f.queue.get(oldest)).toBeUndefined();
    expect((await f.queue.get(recent))!.state).toBe('targetAccepted'); expect((await f.queue.get(concurrent))!.state).toBe('targetAccepted');
    expect((await f.store.read([{ collection: 'message_outbox' }])).sets[0]).toHaveLength(1000);
});
