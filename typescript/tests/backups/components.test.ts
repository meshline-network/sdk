import { afterEach, expect, test, vi } from 'vitest';
import { BackupSelection, MeshlineClient, canonicalJson, concatBytes, contactRecordCodec, createIdentifier, directMessageCodec, encodeBase64Url, encryptionPublicKey, type BackupOptions, type JsonObject, type RecordQuery } from '@meshline/sdk';
import { ClientNetwork } from '../support/client-network.js';
import { createConversationFixture } from '../support/conversation-fixture.js';
import { context } from '../support/relay-fixture.js';
import { vector } from '../support/vectors.js';
import { base64 } from '../../packages/sdk/dist/backups/data.js';
import { writeBackup } from '../../packages/sdk/dist/backups/codec.js';
import { segmentSize } from '../../packages/sdk/dist/backups/streams.js';
import { messageDevice } from '../support/message-fixture.js';
import { systemRandom } from '@meshline/sdk';
import { groupVectors } from '../support/group-fixture.js';
import { GroupRepository, groupKey } from '../../packages/sdk/dist/groups/repository.js';
import { groupEventCodec, deviceCertificateCodec } from '@meshline/sdk';

const vectors = vector<{ group_restore: { records: JsonObject[]; expected_steps: { sequence: number; epoch: number; projection: { head: string; status: string; members: JsonObject } }[] }; epoch_restore: { record: JsonObject } }>('backup');
const resources: ClientNetwork[] = []; afterEach(async () => { vi.restoreAllMocks(); for (const value of resources.splice(0)) await value.dispose(); });
function network() { const result = new ClientNetwork(); resources.push(result); return result; }
async function* records(...values: JsonObject[]): AsyncGenerator<JsonObject> { yield* values; }
async function list(source: AsyncIterable<JsonObject>): Promise<JsonObject[]> { const result = []; for await (const value of source) result.push(value); return result; }
async function* bytes(source: Uint8Array): AsyncGenerator<Uint8Array> { yield source; }
async function backup(client: Awaited<ReturnType<ClientNetwork['open']>>['client'], options?: BackupOptions): Promise<Uint8Array> {
    const output: Uint8Array[] = []; await client.exportBackup({ write: async value => { output.push(value.slice()); } }, 'test password 🔐', options); return concatBytes(...output);
}
async function history(client: Awaited<ReturnType<ClientNetwork['open']>>['client']) { const reader = await client.messageManager.getMessageHistory(); try { return await reader.readNext(100); } finally { await reader.dispose(); } }

test('default history selections are none, immutable and normalized', () => {
    expect(BackupSelection.only([])).toBe(BackupSelection.none); expect(BackupSelection.only(['z', 'a', 'a']).ids).toEqual(['a', 'z']);
    expect(Object.isFrozen(BackupSelection.all)).toBe(true); expect(Object.isFrozen(BackupSelection.only(['a']).ids)).toBe(true);
});
test('components merge contacts by timestamp and leave local requests untouched', async () => {
    const n = network(); const f = await n.open(); const peer = (await n.open(92)).client.accountId; const changes: unknown[] = []; f.client.messageManager.on('contactChanged', value => { changes.push(value); });
    const contact = (alias: string, updatedAt: number, status: 'active' | 'deleted' = 'active') => ({ type: 'contact', data: contactRecordCodec.encode({ account: peer, alias, status, updatedAt }) });
    expect(await f.client.messageManager.importBackup(records(contact('restored', n.clock.wall)))).toEqual({ applied: 1, unchanged: 0, conflicts: 0 });
    expect(await f.client.messageManager.importBackup(records(contact('restored', n.clock.wall)))).toEqual({ applied: 0, unchanged: 1, conflicts: 0 });
    expect(await f.client.messageManager.importBackup(records(contact('older', n.clock.wall - 1)))).toEqual({ applied: 0, unchanged: 1, conflicts: 0 });
    expect(await f.client.messageManager.importBackup(records(contact('different', n.clock.wall)))).toEqual({ applied: 0, unchanged: 0, conflicts: 1 });
    const snapshot = await f.store.read([]); await f.store.commit(snapshot.version, [{ kind: 'put', collection: 'contact_requests', key: peer, value: { sentinel: 'new local intent' } }]);
    expect((await f.client.messageManager.importBackup(records(contact('deleted', n.clock.wall + 1, 'deleted')))).applied).toBe(1);
    expect((await f.store.read([{ collection: 'contact_requests' }])).sets[0]![0]!.value.sentinel).toBe('new local intent'); expect(changes).toHaveLength(2);
});
test('incremental component failure preserves the committed prefix and retry does not repeat notifications', async () => {
    const n = network(); const f = await n.open(); const changed = vi.fn(); f.client.channelManager.on('followChanged', changed);
    const valid = { type: 'channel', data: { channel_id: 'chan_' + encodeBase64Url(systemRandom.bytes(16)), relay_id: n.relays[0]!.descriptor.relayId, is_followed: true } };
    await expect(f.client.channelManager.importBackup(records(valid, { type: 'channel', data: { invalid: true } }))).rejects.toThrow();
    expect(changed).toHaveBeenCalledTimes(1); expect((await f.client.channelManager.importBackup(records(valid))).unchanged).toBe(1); expect(changed).toHaveBeenCalledTimes(1);
});
test('direct import preserves local positions, merges by identity and emits changes only after commit', async () => {
    const n = network(); const f = await createConversationFixture(n); const first = await f.incoming('local'); await f.client.markRead(first.key.sender, first.localSequence);
    const data = { sender: first.key.sender, recipient: f.client.accountId, message_id: createIdentifier('message'), sender_device_id: first.senderDeviceId, created_at: first.createdAt, payload: directMessageCodec.encode({ body: { contentType: 'text/plain', text: 'imported' } }) };
    const notify = vi.fn(); f.client.messageManager.on('messageReceived', notify);
    expect((await f.client.messageManager.importBackup(records({ type: 'message', data }))).applied).toBe(1); const imported = (await history(f.client))[1]!; expect(imported.localSequence).toBeGreaterThan(first.localSequence);
    expect((await f.client.getConversation(first.key.sender))!.unreadCount).toBe(1);
    expect((await f.client.messageManager.importBackup(records({ type: 'message', data }))).unchanged).toBe(1); expect(notify).toHaveBeenCalledTimes(1);
    expect((await f.client.messageManager.importBackup(records({ type: 'message', data: { ...data, created_at: data.created_at + 1 } }))).conflicts).toBe(1);
});
test('group restore reconstructs every independent management projection and retries without replay', async () => {
    const n = network(); const f = await n.open(); const events = vi.fn(); f.client.groupManager.on('groupChanged', events);
    const [reference, ...timeline] = vectors.group_restore.records; await f.client.groupManager.importBackup(records(reference!));
    for (const [index, entry] of timeline.entries()) {
        await f.client.groupManager.importBackup(records(entry)); const state = (await f.store.read([{ collection: 'groups' }])).sets[0]![0]!.value.projection as JsonObject; const expected = vectors.group_restore.expected_steps[index]!;
        expect(state.sequence).toBe(expected.sequence); expect(state.epoch).toBe(expected.epoch); expect(state.managementHash).toBe(expected.projection.head);
        expect((state.members as JsonObject[]).map(value => value.account).sort()).toEqual(Object.keys(expected.projection.members).sort());
    }
    const count = events.mock.calls.length; expect((await f.client.groupManager.importBackup(records(...vectors.group_restore.records))).unchanged).toBe(vectors.group_restore.records.length); expect(events).toHaveBeenCalledTimes(count);
});
test('group conflicts block dependent records only within the same import', async () => {
    const f = await network().open(); const [group, event] = vectors.group_restore.records;
    await f.client.groupManager.importBackup(records(group!)); const data = group!.data as JsonObject;
    const conflict = { type: 'group', data: { ...data, relay_id: '0x' + '9'.repeat(40) } };
    expect(await f.client.groupManager.importBackup(records(conflict, event!))).toEqual({ applied: 0, unchanged: 1, conflicts: 1 });
    expect((await f.client.groupManager.importBackup(records(event!))).applied).toBe(1);
});
function chatRecord(): JsonObject {
    const source = groupVectors.management_chain.chain.find(value => (value.event.payload as JsonObject).$type === 'meshline.group.message')!.event;
    const payload = source.payload as JsonObject; const actor = groupVectors.management_chain.actors.find(value => value.device_id === source.signer_device_id)!;
    return { type: 'group_event', data: { group_id: groupVectors.management_chain.group_id, sequence: source.sequence!, epoch: source.epoch!, payload_json: canonicalJson(payload), certificate_json: canonicalJson(actor.certificate), message_id: payload.message_id!, sender: actor.certificate.account!, sender_device_id: actor.device_id, created_at: new Date(Number(payload.created_at) * 1000).toISOString(), decrypted_payload_json: null, rejection: null, is_message: false } };
}
test('older undecrypted group chat fills history and processing indexes without rewinding the verified projection', async () => {
    const f = await network().open(); await f.client.groupManager.importBackup(records(...vectors.group_restore.records)); const incoming = chatRecord();
    expect((await f.client.groupManager.importBackup(records(incoming))).applied).toBe(1);
    const snapshot = await f.store.read(['groups', 'group_message_ids', 'group_pending_messages'].map(collection => ({ collection })));
    expect((snapshot.sets[0]![0]!.value.projection as JsonObject).sequence).toBe(24); expect(snapshot.sets[1]!.map(row => row.value.sequence)).toEqual([23]); expect(snapshot.sets[2]!.map(row => row.value.sequence)).toEqual([23]);
    expect((await f.client.groupManager.importBackup(records(incoming))).unchanged).toBe(1);
});
test('retained rejected messages may lack a certificate and still advance through the normal rejection flow', async () => {
    const f = await network().open(); await f.client.groupManager.importBackup(records(...vectors.group_restore.records.slice(0, -1))); const incoming = chatRecord();
    incoming.data = { ...incoming.data as JsonObject, certificate_json: null, rejection: 'certificate unavailable' };
    expect((await f.client.groupManager.importBackup(records(incoming))).applied).toBe(1);
    const snapshot = await f.store.read([{ collection: 'groups' }, { collection: 'group_events' }]); expect((snapshot.sets[0]![0]!.value.projection as JsonObject).sequence).toBe(23); expect(snapshot.sets[1]!.at(-1)!.value.rejection).toBeDefined();
});
test.each(['new', 'historical', 'no certificate', 'partial historical'] as const)('rejected group %s records retain metadata and identity without replaying content', async mode => {
    const historical = mode.includes('historical');
    const f = await network().open(); await f.client.groupManager.importBackup(records(...(historical ? vectors.group_restore.records : vectors.group_restore.records.slice(0, -1))));
    const incoming = chatRecord(); const data = { ...incoming.data as JsonObject, rejection: 'invalid message content' } as JsonObject;
    if (mode === 'no certificate') data.certificate_json = null;
    if (mode === 'partial historical') { data.sender_device_id = null; data.created_at = null; }
    incoming.data = data; const changed = vi.fn(); f.client.groupManager.on('timelineChanged', changed);
    expect((await f.client.groupManager.importBackup(records(incoming))).applied).toBe(1);
    expect((await f.client.groupManager.importBackup(records(incoming))).unchanged).toBe(1);
    const exported = (await list(f.client.groupManager.exportBackup(BackupSelection.all))).find(row => row.type === 'group_event' && (row.data as JsonObject).sequence === data.sequence)!;
    expect(exported).toEqual(mode === 'no certificate' ? { ...incoming, data: { ...data, rejection: expect.any(String) } } : incoming); expect(changed).not.toHaveBeenCalled();
    const snapshot = await f.store.read([{ collection: 'group_message_ids' }, { collection: 'group_pending_messages' }]);
    expect(snapshot.sets[0]!.map(row => row.value.sequence)).toEqual([data.sequence]); expect(snapshot.sets[1]).toEqual([]);
    if (historical) return; // The fixture closes the group after this message.
    const group = { groupId: String(data.group_id), relayId: String((vectors.group_restore.records[0]!.data as JsonObject).relay_id) };
    const repository = new GroupRepository(f.store, context, f.client.accountId);
    const projection = await repository.get(group); const source = groupVectors.management_chain.chain.find(value => (value.event.payload as JsonObject).$type === 'meshline.group.message')!.event;
    const event = groupEventCodec.decode({ ...source, sequence: projection!.sequence + 1, epoch: projection!.epoch });
    const certificate = deviceCertificateCodec.parse(String((chatRecord().data as JsonObject).certificate_json));
    const initial = (await f.store.read([groupKey(group)])).sets[0]![0]!.value;
    const duplicate = await repository.applyPage(group, { events: [event], certificates: [certificate], hasMore: false }, initial);
    expect(duplicate.rejected[0]!.error.code).toBe('duplicate_message'); expect(duplicate.messageSequences).toEqual([]);
});
test('epoch keys round trip without a group reference, conflicts preserve the whole epoch and do not fan out', async () => {
    const n = network(); const f = await n.open(); await f.client.deviceManager.createDevice(3600); const source = vectors.epoch_restore.record; const data = source.data as JsonObject;
    expect((await f.client.groupManager.importBackup(records(source))).applied).toBe(1); expect((await f.client.groupManager.importBackup(records(source))).unchanged).toBe(1);
    const empty = { type: 'group_epoch', data: { ...data, epoch: 8, client_secret: null, application_secret: null } }; await f.client.groupManager.importBackup(records(empty));
    const current = await f.store.read([{ collection: 'group_epochs' }]);
    await f.store.commit(current.version, [{ kind: 'put', collection: 'group_client_secrets', key: `${data.group_id}|${data.commitment}`, value: { commitment: data.commitment!, protectedSecret: current.sets[0]![0]!.value.protectedClientSecret! } }]);
    const exported = await list(f.client.groupManager.exportBackup()); expect(exported).toContainEqual(source); expect(exported).toContainEqual(empty);
    const bad = { type: 'group_epoch', data: { ...data, application_secret: base64(new Uint8Array(32).fill(2)), member_public_key: null } };
    expect((await f.client.groupManager.importBackup(records(bad))).conflicts).toBe(1); expect(await list(f.client.groupManager.exportBackup())).toEqual(exported);
});
test.each(['deviceManager', 'messageManager', 'groupManager', 'channelManager'] as const)('%s disposal interrupts an input iterator stalled between records', async manager => {
    const n = network(); const f = await n.open(); let pull!: () => void; const entered = new Promise<void>(resolve => { pull = resolve; });
    const source: AsyncIterable<JsonObject> = { [Symbol.asyncIterator]: () => ({ next: () => { pull(); return new Promise(() => {}); }, return: async () => ({ done: true, value: undefined }) }) };
    const importing = expect(f.client[manager].importBackup(source)).rejects.toThrow(); await entered; await f.client.dispose(); await importing;
});

test('cancellation after a committed record stops further reads and observes input cleanup failure', async () => {
    const n = network(); const f = await n.open(); const controller = new AbortController(); const reason = new Error('caller canceled');
    const next = vi.fn(async () => ({ done: false as const, value: { type: 'channel', data: { channel_id: 'chan_' + encodeBase64Url(systemRandom.bytes(16)), relay_id: n.relays[0]!.descriptor.relayId, is_followed: true } } }));
    const close = vi.fn(async () => { throw new Error('source cleanup failed'); });
    const source: AsyncIterable<JsonObject> = { [Symbol.asyncIterator]: () => ({ next, return: close }) };
    const unhandled: unknown[] = []; const observe = (error: unknown) => { unhandled.push(error); }; process.on('unhandledRejection', observe);
    f.client.channelManager.on('followChanged', () => controller.abort(reason));
    try {
        await expect(f.client.channelManager.importBackup(source, controller.signal)).rejects.toBe(reason);
        await new Promise(resolve => setImmediate(resolve));
        expect(next).toHaveBeenCalledTimes(1); expect(close).toHaveBeenCalledTimes(1); expect(unhandled).toEqual([]);
        expect((await f.store.read([{ collection: 'channels' }])).sets[0]).toHaveLength(1);
    } finally { process.off('unhandledRejection', observe); }
});

test('whole-file import observes read and close failures when the input cancels during its first pull', async () => {
    const f = await network().open(); const controller = new AbortController(); const reason = new Error('caller canceled');
    const next = vi.fn(() => { controller.abort(reason); return Promise.reject(new Error('source read failed')); });
    const close = vi.fn(async () => { throw new Error('source cleanup failed'); });
    const source: AsyncIterable<Uint8Array> = { [Symbol.asyncIterator]: () => ({ next, return: close }) };
    const unhandled: unknown[] = []; const observe = (error: unknown) => { unhandled.push(error); }; process.on('unhandledRejection', observe);
    try {
        await expect(f.client.importBackup(source, 'password', controller.signal)).rejects.toBe(reason);
        await new Promise(resolve => setImmediate(resolve));
        expect(next).toHaveBeenCalledTimes(1); expect(close).toHaveBeenCalledTimes(1); expect(unhandled).toEqual([]);
    } finally { process.off('unhandledRejection', observe); }
});

test('component cancellation ends whole-file import while input is stalled and preserves its committed prefix', async () => {
    const n = network(); const f = await n.open(); const peer = messageDevice(context, 94, 95);
    const messages: JsonObject[] = Array.from({ length: 20 }, () => ({ type: 'message', data: {
        sender: peer.certificate.account, message_id: createIdentifier('message'), sender_device_id: peer.id,
        recipient: f.client.accountId, created_at: n.clock.wall,
        payload: directMessageCodec.encode({ body: { contentType: 'text/plain', text: encodeBase64Url(systemRandom.bytes(65536)) } }),
    } }));
    const chunks: Uint8Array[] = [];
    await writeBackup({ write: async value => { chunks.push(value.slice()); } }, records(
        { type: 'manifest', data: { network: context.toString(), account: f.client.accountId, created_at: n.clock.wall,
            direct_history: { mode: 'all', ids: [] }, group_history: { mode: 'none', ids: [] } } },
        ...messages, { type: 'end', data: { records: messages.length } },
    ), 'password', systemRandom);
    const file = concatBytes(...chunks); const boundary = 12 + new DataView(file.buffer).getUint32(8) + segmentSize + 1;
    expect(file.length).toBeGreaterThan(boundary);
    let entered!: () => void; const stalled = new Promise<void>(resolve => { entered = resolve; });
    let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; });
    const source = (async function* () { yield file.subarray(0, boundary); entered(); await gate; yield file.subarray(boundary); })();
    const controller = new AbortController(); let outcome: unknown;
    const importing = f.client.importBackup(source, 'password', controller.signal).then(value => { outcome = value; }, error => { outcome = error; });
    try {
        await stalled; const prefix = (await f.store.read([{ collection: 'messages' }])).sets[0]!.length;
        expect(prefix).toBeGreaterThan(0); expect(prefix).toBeLessThan(messages.length);
        await f.client.messageManager.dispose();
        await vi.waitFor(() => expect(outcome).toMatchObject({ name: 'AbortError' }), { timeout: 1000 });
        expect(controller.signal.aborted).toBe(false);
        expect((await f.store.read([{ collection: 'messages' }])).sets[0]).toHaveLength(prefix);
        const reopened = await n.open(91, f.path);
        expect(await reopened.client.importBackup(bytes(file), 'password')).toEqual({ applied: messages.length - prefix, unchanged: prefix, conflicts: 0 });
        expect((await history(reopened.client))).toHaveLength(messages.length);
    } finally {
        controller.abort(); release(); await importing;
        // The component was deliberately disposed independently of its owning client.
        const stop = vi.spyOn(f.client.messageManager, 'stop').mockResolvedValue();
        try { await f.client.dispose(); } finally { stop.mockRestore(); }
    }
});
test('member keys are protected for the local device and checked against their identifier', async () => {
    const f = await network().open(); await f.client.deviceManager.createDevice(3600); const secret = new Uint8Array(32).fill(77);
    const value = { type: 'group_secret', data: { group_id: 'grp_' + encodeBase64Url(systemRandom.bytes(16)), kind: 'member', id: encodeBase64Url(encryptionPublicKey(secret)), secret: base64(secret) } };
    expect((await f.client.groupManager.importBackup(records(value))).applied).toBe(1); expect(await list(f.client.groupManager.exportBackup())).toEqual([value]);
    await expect(f.client.groupManager.importBackup(records({ ...value, data: { ...value.data, id: encodeBase64Url(new Uint8Array(32)) } }))).rejects.toThrow('identifier');
});
test('an epoch with no secrets requires no device; protection failure commits neither secret', async () => {
    const n = network(); const f = await n.open(); const source = vectors.epoch_restore.record; const data = source.data as JsonObject;
    const empty = { type: 'group_epoch', data: { ...data, client_secret: null, application_secret: null } }; expect((await f.client.groupManager.importBackup(records(empty))).applied).toBe(1);
    await f.client.deviceManager.createDevice(3600); let calls = 0; let fail = true;
    const client = new MeshlineClient({ context, accountId: f.client.accountId, store: f.store, relayClients: f.pool, clock: n.clock, secretProtector: {
        protect: async value => { if (++calls === 2 && fail) throw new Error('second secret failed'); return value.slice(); }, unprotect: async value => value.slice(),
    } }); n.resources.push(client); await client.initialize();
    await expect(client.groupManager.importBackup(records(source))).rejects.toThrow('protection failed');
    expect((await f.store.read([{ collection: 'group_epochs' }])).sets[0]![0]!.value.protectedClientSecret).toBeUndefined(); expect((await f.store.read([{ collection: 'group_application_secrets' }])).sets[0]).toEqual([]);
    fail = false; expect((await client.groupManager.importBackup(records(source))).applied).toBe(1); expect(await list(client.groupManager.exportBackup())).toEqual([source]);
});
test('disposal cancels group secret export and drains the active protection operation', async () => {
    const n = network(); const f = await n.open(); await f.client.deviceManager.createDevice(3600); let started!: () => void; const entered = new Promise<void>(resolve => { started = resolve; });
    const client = new MeshlineClient({ context, accountId: f.client.accountId, store: f.store, relayClients: f.pool, clock: n.clock, secretProtector: {
        protect: async value => value.slice(), unprotect: async (_value, _purpose, signal) => { started(); return new Promise((_resolve, reject) => { signal!.addEventListener('abort', () => reject(signal!.reason), { once: true }); }); },
    } }); n.resources.push(client); await client.initialize(); await client.groupManager.importBackup(records(vectors.epoch_restore.record));
    const iterator = client.groupManager.exportBackup(); const pending = expect(iterator.next()).rejects.toThrow(); await entered; await client.dispose(); await pending;
});
test('group event confirmation and projection are committed together while unrelated operations survive', async () => {
    const f = await network().open(); const [group, created, update] = vectors.group_restore.records; await f.client.groupManager.importBackup(records(group!, created!));
    const data = update!.data as JsonObject; const payload = JSON.parse(String(data.payload_json)) as JsonObject; const groupId = String(data.group_id); const relayId = String((group!.data as JsonObject).relay_id);
    // A timeline-confirmed ordinary operation is independent of latest-state group.update coordination.
    const close = vectors.group_restore.records.at(-1)!; const closePayload = JSON.parse(String((close.data as JsonObject).payload_json)) as JsonObject;
    const snapshot = await f.store.read([]); await f.store.commit(snapshot.version, [
        { kind: 'put', collection: 'group_operations', key: `${groupId}|group.close`, value: { groupId, relayId, method: 'group.close', request: closePayload } },
        { kind: 'put', collection: 'group_operations', key: `${groupId}|group.update`, value: { groupId, relayId, method: 'group.update', request: payload } },
    ]);
    await f.client.groupManager.importBackup(records(...vectors.group_restore.records.slice(2)));
    const result = await f.store.read([{ collection: 'group_operations' }, { collection: 'groups', key: groupId }]); expect(result.sets[0]!.map(value => value.value.method)).toEqual(['group.update']); expect(result.sets[1]![0]!.value.locallyClosed).toBe(true);
});
test('whole backups default to no chat, explicit subsets round trip and repeated import preserves target history', async () => {
    const n = network(); const source = await createConversationFixture(n); await source.incoming('selected history');
    const peer = source.peer.certificate.account; await source.client.messageManager.importBackup(records({ type: 'contact', data: contactRecordCodec.encode({ account: peer, alias: 'friend', status: 'active', updatedAt: n.clock.wall }) }));
    const target = await n.open(); const base = await backup(source.client); expect((await target.client.importBackup(bytes(base), 'test password 🔐')).applied).toBe(1); expect(await history(target.client)).toHaveLength(0);
    const selected = await backup(source.client, { directHistory: BackupSelection.only([peer]) }); await target.client.importBackup(bytes(selected), 'test password 🔐'); expect((await history(target.client))[0]!.body!.text).toBe('selected history');
    expect((await target.client.importBackup(bytes(selected), 'test password 🔐')).applied).toBe(0); await target.client.importBackup(bytes(base), 'test password 🔐'); expect(await history(target.client)).toHaveLength(1);
    const other = await n.open(92); await expect(other.client.importBackup(bytes(base), 'test password 🔐')).rejects.toThrow('binding');
}, 30_000);
test('export validates selections before output and detects a dependency changed between component snapshots', async () => {
    const n = network(); const f = await createConversationFixture(n); const write = vi.fn(async () => {});
    await expect(f.client.exportBackup({ write }, 'password', { directHistory: BackupSelection.only(['invalid']) })).rejects.toThrow(); expect(write).not.toHaveBeenCalled();
    const original = f.client.channelManager.exportBackup.bind(f.client.channelManager);
    vi.spyOn(f.client.channelManager, 'exportBackup').mockImplementation(async function* (signal) {
        yield* original(signal); const snapshot = await f.store.read([{ collection: 'device_states', key: f.client.accountId }]); await f.store.commit(snapshot.version, [{ kind: 'delete', collection: 'device_states', key: f.client.accountId }]);
    });
    await expect(backup(f.client)).rejects.toThrow('disappeared');
}, 30_000);
test('logical end count failure retains prior imports and refuses to report success', async () => {
    const n = network(); const f = await n.open(); const channel = { type: 'channel', data: { channel_id: 'chan_' + encodeBase64Url(systemRandom.bytes(16)), relay_id: n.relays[0]!.descriptor.relayId, is_followed: true } }; const output: Uint8Array[] = [];
    const manifest = { type: 'manifest', data: { network: context.toString(), account: f.client.accountId, created_at: n.clock.wall, direct_history: { mode: 'none', ids: [] }, group_history: { mode: 'none', ids: [] } } };
    await writeBackup({ write: async value => { output.push(value.slice()); } }, records(manifest, channel, { type: 'end', data: { records: 2 } }), 'password', systemRandom);
    await expect(f.client.importBackup(bytes(concatBytes(...output)), 'password')).rejects.toThrow('count'); expect((await list(f.client.channelManager.exportBackup()))).toEqual([channel]);
}, 30_000);

test('message export selects indexed history in local order and keeps contacts and messages at one version', async () => {
    const n = network(); const f = await n.open(); const peers = [94, 96, 98].map(seed => messageDevice(context, seed, seed + 1));
    const messages = Array.from({ length: 135 }, (_, index) => ({ type: 'message', data: {
        sender: peers[index % 3]!.certificate.account, recipient: f.client.accountId, message_id: createIdentifier('message'),
        sender_device_id: peers[index % 3]!.id, created_at: n.clock.wall,
        payload: directMessageCodec.encode({ body: { contentType: 'text/plain', text: `message ${index}` } }),
    } }));
    const contact = { type: 'contact', data: contactRecordCodec.encode({ account: peers[0]!.certificate.account, alias: 'original', status: 'active', updatedAt: n.clock.wall }) };
    await f.client.messageManager.importBackup(records(contact, ...messages));
    const reader = await f.client.messageManager.getMessageHistory(); await reader.dispose();
    const open = f.store.openSnapshot.bind(f.store); const queries: RecordQuery[] = []; const dispose = vi.fn();
    vi.spyOn(f.store, 'openSnapshot').mockImplementation(async (collections, signal) => {
        const snapshot = await open(collections, signal);
        return { read: (requests, token) => { queries.push(...requests); return snapshot.read(requests, token); }, dispose: async () => { dispose(); await snapshot.dispose(); } };
    });
    expect(await list(f.client.messageManager.exportBackup())).toEqual([contact]);
    expect(queries.every(query => query.collection === 'contacts')).toBe(true); queries.length = 0;
    const selected = [peers[0]!.certificate.account, peers[2]!.certificate.account];
    const iterator = f.client.messageManager.exportBackup(BackupSelection.only(selected)); const first = await iterator.next(); expect(first.value).toEqual(contact);
    await f.client.messageManager.importBackup(records({ type: 'contact', data: contactRecordCodec.encode({ account: peers[0]!.certificate.account, alias: 'newer', status: 'active', updatedAt: n.clock.wall + 1 }) }));
    const live = await f.store.read([]); const removed = messages[0]!.data;
    await f.store.commit(live.version, [{ kind: 'delete', collection: 'messages', key: `${removed.sender}|${removed.message_id}` }]);
    const exported = [first.value!, ...await list(iterator)]; const expected = messages.filter(value => selected.includes(value.data.sender));
    expect(exported).toEqual([contact, ...expected]);
    expect(queries.filter(query => query.collection === 'messages').every(query => query.key !== undefined && expected.some(value => query.key === `${value.data.sender}|${value.data.message_id}`))).toBe(true);
    expect(queries.filter(query => query.key === undefined).every(query => query.limit === 64)).toBe(true); expect(dispose).toHaveBeenCalledTimes(2);
    const target = await n.open(); await target.client.messageManager.importBackup(records(...exported));
    const targetReader = await target.client.messageManager.getMessageHistory();
    try { expect((await targetReader.readNext(200)).map(value => value.key.messageId)).toEqual(expected.map(value => value.data.message_id)); } finally { await targetReader.dispose(); }
});

test('group export pages events within the same snapshot as references and secrets', async () => {
    const f = await network().open(); await f.client.establishAccount(); await f.client.groupManager.importBackup(records(...vectors.group_restore.records, vectors.epoch_restore.record));
    const state = await f.store.read([{ collection: 'group_events' }]); const template = state.sets[0]![0]!;
    const extra = Array.from({ length: 130 }, (_, index) => ({ kind: 'put' as const, collection: 'group_events', key: `${template.key.split('|')[0]}|${String(100 + index).padStart(16, '0')}`,
        value: { ...template.value, event: { ...(template.value.event as JsonObject), sequence: 100 + index } } }));
    await f.store.commit(state.version, extra);
    const expected = await list(f.client.groupManager.exportBackup()); const queries: RecordQuery[] = []; const open = f.store.openSnapshot.bind(f.store);
    vi.spyOn(f.store, 'openSnapshot').mockImplementation(async (collections, signal) => {
        const snapshot = await open(collections, signal);
        return { read: (requests, token) => { queries.push(...requests); return snapshot.read(requests, token); }, dispose: () => snapshot.dispose() };
    });
    const iterator = f.client.groupManager.exportBackup(); const first = await iterator.next(); expect(first.value!.type).toBe('group');
    const live = await f.store.read([{ collection: 'group_events' }, { collection: 'group_epochs' }, { collection: 'group_application_secrets' }]);
    await f.store.commit(live.version, live.sets.flat().map(row => ({ kind: 'delete' as const, collection: row.collection, key: row.key })));
    expect([first.value!, ...await list(iterator)]).toEqual(expected);
    expect(queries.filter(query => query.collection === 'group_events').length).toBeGreaterThan(2);
    expect(queries.filter(query => query.key === undefined).every(query => query.limit === 64)).toBe(true);
});

test('canceling an export between consumer pulls releases its fixed snapshot', async () => {
    const n = network(); const f = await n.open(); const peer = messageDevice(context, 94, 95).certificate.account;
    await f.client.messageManager.importBackup(records({ type: 'contact', data: contactRecordCodec.encode({ account: peer, status: 'active', updatedAt: n.clock.wall }) }));
    const open = f.store.openSnapshot.bind(f.store); const dispose = vi.fn();
    vi.spyOn(f.store, 'openSnapshot').mockImplementation(async (collections, signal) => {
        const snapshot = await open(collections, signal);
        return { read: snapshot.read.bind(snapshot), dispose: async () => { dispose(); await snapshot.dispose(); } };
    });
    const controller = new AbortController(); const iterator = f.client.messageManager.exportBackup(BackupSelection.none, controller.signal);
    expect((await iterator.next()).done).toBe(false); controller.abort(new Error('stop export'));
    await vi.waitFor(() => expect(dispose).toHaveBeenCalledOnce()); await expect(iterator.next()).rejects.toThrow('stop export'); expect(dispose).toHaveBeenCalledOnce();
});
