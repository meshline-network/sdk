import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import { NodeSqliteStore } from '@meshline/storage-node';
import { decodeBase64Url, groupEventCodec, groupKeyPageCodec, groupSyncPageCodec, type GroupEvent, type GroupSecretBox, type JsonObject } from '@meshline/sdk';
import { GroupRepository } from '../../packages/sdk/dist/groups/repository.js';
import { groupActors, groupContext as context, groupProjectionWire, groupReference as group, groupVectors } from '../support/group-fixture.js';
import { removeTestDirectory } from '../support/temp.js';
const data = groupVectors.management_chain; const resources: NodeSqliteStore[] = []; const directories: string[] = [];
afterEach(async () => { for (const store of resources.splice(0)) await store.dispose(); for (const directory of directories.splice(0)) await removeTestDirectory(directory); });
const events = [...data.chain.map(row => groupEventCodec.decode(row.event)), groupEventCodec.decode(data.automatic_rotation.event)].sort((a, b) => a.sequence - b.sequence);
const box = (seed: number): GroupSecretBox => ({ alg: 'X25519-HKDF-SHA256-AES256GCM', enc: new Uint8Array(32).fill(seed), sealedSecret: new Uint8Array(60).fill(seed + 1) });
async function fixture(actor = 'A') {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-group-store-')); directories.push(directory); const path = join(directory, 'state.sqlite');
    const accountId = groupActors.get(actor)!.certificate.account; const store = new NodeSqliteStore(path); resources.push(store); await store.migrate(); await store.initialize({ context: context.toString(), accountId });
    const repository = new GroupRepository(store, context, accountId);
    let timeline = events; let reads = 0; let beforeRead: (() => Promise<void>) | undefined;
    const source = { async read(query: { after?: number }) { reads++; await beforeRead?.(); return { events: timeline.filter(entry => entry.sequence > (query.after ?? -1)), certificates: [...groupActors.values()].map(actor => actor.certificate), hasMore: false }; } };
    return { store, path, accountId, repository, source, get reads() { return reads; }, set timeline(value: GroupEvent[]) { timeline = value; }, set beforeRead(value: (() => Promise<void>) | undefined) { beforeRead = value; } };
}
test('verified state, epoch authority, signed events, message identity and synchronization cursor commit at one revision', async () => {
    const f = await fixture(); const result = await f.repository.synchronizePage(group, f.source); expect(result.rejected).toEqual([]); expect(result.messageSequences).toEqual([23]);
    expect(groupProjectionWire(result.projection!)).toEqual(data.chain.at(-1)!.expected_projection); expect(result.projection!.sequence).toBe(24);
    const snapshot = await f.store.read(['groups', 'group_events', 'group_epochs', 'group_message_ids', 'group_pending_messages'].map(collection => ({ collection })));
    expect(snapshot.sets[1]!.length).toBe(events.length); expect(new Set(snapshot.sets.flat().map(row => row.revision)).size).toBe(1);
    const epochs = await f.repository.epochs(group); expect(epochs.map(entry => entry.epoch)).toEqual([0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20]);
    expect(epochs.at(-1)!.memberPublicKey).toBeUndefined(); expect(epochs[0]!.memberPublicKey).toEqual(decodeBase64Url(groupActors.get('A')!.member_encryption_public_key));
});
test('SQLite restart restores verified chain and resumes after the exact saved position', async () => {
    const f = await fixture('B'); f.timeline = events.filter(entry => entry.sequence <= 8); await f.repository.synchronizePage(group, f.source); await f.store.dispose();
    const reopened = new NodeSqliteStore(f.path); resources.push(reopened); await reopened.initialize({ context: context.toString(), accountId: f.accountId });
    const repository = new GroupRepository(reopened, context, f.accountId); expect((await repository.get(group))!.sequence).toBe(8); f.timeline = events;
    const result = await repository.synchronizePage(group, { async read(query) { expect(query.after).toBe(8); return f.source.read(query); } });
    expect(groupProjectionWire(result.projection!)).toEqual(data.chain.at(-1)!.expected_projection); expect(result.projection!.members.find(member => member.account === f.accountId)!.joinedAtSequence).toBe(12);
    const version = (await reopened.read([])).version; await repository.synchronizePage(group, f.source); expect((await reopened.read([])).version).toBe(version);
});
test('invalid late administration event rolls back all preceding uncommitted projection, evidence and epoch changes', async () => {
    const f = await fixture(); const invalid = groupEventCodec.decode(data.chain[2]!.event); invalid.payload.device_signature = 'A'.repeat(86);
    f.timeline = [events[0]!, events[1]!, invalid]; await expect(f.repository.synchronizePage(group, f.source)).rejects.toThrow();
    expect(await f.repository.get(group)).toBeUndefined(); expect(await f.repository.epochs(group)).toEqual([]);
    expect((await f.store.read([{ collection: 'group_events' }, { collection: 'group_message_ids' }])).sets).toEqual([[], []]);
});
test('a rejected ordinary message records diagnostics while later verified administration still commits', async () => {
    const f = await fixture(); const corrupt = groupEventCodec.decode(data.chain[14]!.event); corrupt.payload.device_signature = 'A'.repeat(86);
    f.timeline = events.map(event => event.sequence === corrupt.sequence ? corrupt : event);
    const result = await f.repository.synchronizePage(group, f.source); expect(result.rejected.map(row => row.sequence)).toEqual([23]); expect(result.messageSequences).toEqual([]); expect(result.projection!.state.status).toBe('closed');
    expect((await f.repository.event(group, 23))!.rejection).toBeDefined(); expect((await f.store.read([{ collection: 'group_message_ids' }, { collection: 'group_pending_messages' }])).sets).toEqual([[], []]);
});
test('duplicate logical messages are quarantined within a page and after restart without replacing the first event', async () => {
    const f = await fixture(); const message = groupEventCodec.decode(data.chain[14]!.event); f.timeline = [...events.filter(entry => entry.sequence <= 23), { ...message, sequence: 24 }];
    const result = await f.repository.synchronizePage(group, f.source); expect(result.messageSequences).toEqual([23]); expect(result.rejected.map(row => row.error.code)).toEqual(['duplicate_message']);
    f.timeline = [{ ...message, sequence: 25 }]; const later = await f.repository.synchronizePage(group, f.source); expect(later.rejected[0]!.error.code).toBe('duplicate_message'); expect(later.projection!.sequence).toBe(25);
    expect((await f.store.read([{ collection: 'group_message_ids' }])).sets[0]!.map(row => row.value.sequence)).toEqual([23]); expect((await f.repository.event(group, 23))!.rejection).toBeUndefined();
});
test('storage failure never advances the cursor and a retry sees the same event evidence', async () => {
    const f = await fixture(); const commit = f.store.commit.bind(f.store); let fail = true;
    f.store.commit = async (version, changes, signal) => { if (fail) { fail = false; throw new Error('disk temporarily unavailable'); } return commit(version, changes, signal); };
    await expect(f.repository.synchronizePage(group, f.source)).rejects.toThrow('disk temporarily'); expect(await f.repository.get(group)).toBeUndefined();
    await f.repository.synchronizePage(group, f.source); expect((await f.repository.get(group))!.sequence).toBe(24); expect(f.reads).toBe(2);
});
test('CAS contention retries local planning without repeating network reads or overwriting concurrent group state', async () => {
    const f = await fixture(); const commit = f.store.commit.bind(f.store); let collide = true;
    f.store.commit = async (version, changes, signal) => { if (collide) { collide = false; await commit(version, [{ kind: 'put', collection: 'unrelated', key: 'other', value: { preserved: true } }], signal); } return commit(version, changes, signal); };
    await f.repository.synchronizePage(group, f.source); expect(f.reads).toBe(1); expect((await f.repository.get(group))!.sequence).toBe(24);
    const other = await fixture(); other.beforeRead = async () => { other.beforeRead = undefined; await other.repository.synchronizePage(group, other.source); };
    await expect(other.repository.synchronizePage(group, other.source)).rejects.toThrow('changed'); expect((await other.repository.get(group))!.sequence).toBe(24);
});
test('key page expansion requires verified epochs and carries omitted client boxes only while commitment and member key match', async () => {
    const f = await fixture('C'); await f.repository.synchronizePage(group, f.source);
    const page = { keys: [{ epoch: 4, clientSecretBox: box(1), relaySecretBox: box(2) }, { epoch: 6, clientSecretBox: box(3), relaySecretBox: box(4) }, { epoch: 8, relaySecretBox: box(5) }], hasMore: false };
    await f.repository.saveKeyPage(group, page, -1); expect(await f.repository.keyProgress(group)).toBe(8);
    const epochs = await f.repository.epochs(group); expect(epochs.find(row => row.epoch === 8)!.keyEntry!.clientSecretBox).toEqual(box(3));
    const snapshot = await f.store.read([{ collection: 'group_epochs' }]); expect(new Set(snapshot.sets[0]!.filter(row => row.value.keyEntry).map(row => row.revision)).size).toBe(1);
    await expect(f.repository.saveKeyPage(group, { keys: [{ epoch: 22, clientSecretBox: box(1), relaySecretBox: box(2) }], hasMore: false }, 8)).rejects.toThrow('established');
    await expect(f.repository.saveKeyPage(group, { keys: [{ epoch: 0, clientSecretBox: box(1), relaySecretBox: box(2) }], hasMore: false }, -1)).rejects.toThrow('accessible');
    // Legitimate re-wrapping may change nonce/ciphertext. Derived secrets are checked independently before use.
    await f.repository.saveKeyPage(group, { keys: [{ epoch: 8, clientSecretBox: box(7), relaySecretBox: box(8) }], hasMore: false }, 6);
    expect((await f.repository.epochs(group)).find(row => row.epoch === 8)!.keyEntry!.clientSecretBox).toEqual(box(7));
});
test('missing required client boxes roll back the complete key page, including earlier valid entries', async () => {
    const f = await fixture('C'); await f.repository.synchronizePage(group, f.source);
    await expect(f.repository.saveKeyPage(group, { keys: [{ epoch: 4, clientSecretBox: box(1), relaySecretBox: box(2) }, { epoch: 6, relaySecretBox: box(3) }], hasMore: false }, -1)).rejects.toThrow('changed');
    expect(await f.repository.keyProgress(group)).toBe(-1);
    await expect(f.repository.saveKeyPage(group, { keys: [{ epoch: 6, clientSecretBox: box(1), relaySecretBox: box(2) }, { epoch: 10, relaySecretBox: box(3) }], hasMore: false }, -1)).rejects.toThrow('changed');
    expect(await f.repository.keyProgress(group)).toBe(-1);
    await expect(f.repository.saveKeyPage(group, { keys: [{ epoch: 6, relaySecretBox: box(3) }], hasMore: false }, -1)).rejects.toThrow('first');
});
test('page structure and hosting-relay binding are checked before persistence', async () => {
    const f = await fixture(); await f.repository.synchronizePage(group, f.source); const elsewhere = { ...group, relayId: '0x' + 'f'.repeat(40) };
    await expect(f.repository.event(elsewhere, 0)).rejects.toThrow('another hosting'); await expect(f.repository.epochs(elsewhere)).rejects.toThrow('another hosting');
    expect(() => groupKeyPageCodec.decode({ keys: [{ epoch: 0, client_secret_box: null, relay_secret_box: {} }], has_more: false })).toThrow();
    const unknown = await fixture(); const wire = { ...data.sync_response, events: [...data.chain.slice(0, 2).map(row => row.event)].reverse() } as JsonObject;
    await expect(unknown.repository.synchronizePage(group, { async read() { return groupSyncPageCodec.decode(wire); } })).rejects.toThrow('order'); expect(await unknown.repository.get(group)).toBeUndefined();
});
