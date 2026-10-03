import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test, vi } from 'vitest';
import { ProtocolError, StateConflictError, certificateId, concatBytes, decryptAes, encryptAes, encodeUtf8, encryptMessage, systemRandom,
    type MeshlineStore, type SecretProtector, type MessageTimelineEntry } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { MessageRepository, messageKey, type MessageEffects } from '../../packages/sdk/dist/messages/repository.js';
import { MessageOutbox, type OutboxRecord } from '../../packages/sdk/dist/messages/outbox.js';
import { messageDevice } from '../support/message-fixture.js';
import { context } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { removeTestDirectory } from '../support/temp.js';

const directories: string[] = []; const stores: MeshlineStore[] = [];
afterEach(async () => { for (const store of stores.splice(0)) await store.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
const from = messageDevice(context, 1, 2); const to = messageDevice(context, 3, 4);
const relayA = `0x${'1'.repeat(40)}`; const relayB = `0x${'2'.repeat(40)}`; const id = 'msg_AAECAwQFBgcICQoLDA0ODw';
const direct = { $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: 'durable message 中文😀' } };
class Protector implements SecretProtector {
    readonly key = new Uint8Array(32).fill(11); readonly inputs: Uint8Array[] = []; readonly plaintexts: Uint8Array[] = []; readonly purposes: string[] = [];
    fail = false;
    async protect(plaintext: Uint8Array, purpose: string) { this.inputs.push(plaintext); this.purposes.push(purpose); if (this.fail) throw new Error('keychain unavailable');
        const nonce = systemRandom.bytes(12); return concatBytes(nonce, encryptAes(this.key, nonce, plaintext, encodeUtf8(purpose))); }
    async unprotect(ciphertext: Uint8Array, purpose: string) { this.purposes.push(purpose); const bytes = decryptAes(this.key, ciphertext.slice(0, 12), ciphertext.slice(12), encodeUtf8(purpose)); this.plaintexts.push(bytes); return bytes; }
}
async function fixture() {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-message-store-')); directories.push(directory); const path = join(directory, 'state.sqlite');
    const clock = new AdvancingClock(); const store = new NodeSqliteStore(path); stores.push(store); await store.migrate(); await store.initialize({ context: context.toString(), accountId: to.certificate.account });
    const protector = new Protector(); const options = { store, context, accountId: to.certificate.account, deviceId: () => to.id, clock, secretProtector: protector };
    const repo = new MessageRepository(options);
    const request = await encryptMessage({ context, signer: from, messageId: id, createdAt: clock.wall, recipient: to.certificate.account, recipientDevices: [to.certificate], payload: direct });
    const entry: MessageTimelineEntry = { sequence: 7, envelope: request.envelope, keyBox: request.recipientBoxes[0]!, acceptedAt: clock.wall };
    const prepared = await repo.prepare(entry.envelope, direct);
    return { clock, store, path, repo, request, entry, prepared, protector, options };
}

test('message, business effect and timeline cursor commit together and survive reopen', async () => {
    const f = await fixture(); const effects: MessageEffects = { queries: [{ collection: 'test_contacts', key: 'peer' }], plan(snapshot) {
        expect(snapshot.sets).toHaveLength(1); return [{ kind: 'put', collection: 'test_contacts', key: 'peer', value: { updated: true } }];
    } };
    expect(await f.repo.accept(relayA, f.entry, f.prepared, false, effects)).toEqual({ advanced: true, inserted: true });
    expect(await f.repo.getProgress(relayA)).toMatchObject({ sequence: 7, hasRetentionGap: false });
    expect((await f.store.read([{ collection: 'test_contacts' }])).sets[0]![0]!.value).toEqual({ updated: true });
    await f.store.dispose(); const reopened = new NodeSqliteStore(f.path); stores.push(reopened); await reopened.initialize({ context: context.toString(), accountId: to.certificate.account });
    const repo = new MessageRepository({ ...f.options, store: reopened }); expect((await repo.readTimeline(0, 20))[0]).toMatchObject({ localSequence: 1, payload: direct });
    expect((await repo.get({ sender: from.certificate.account, messageId: id }))!.body!.text).toBe(direct.body.text);
    expect((await repo.getProgress(relayA)).sequence).toBe(7);
});

test('effect failures leave messages and cursors untouched; permanent rejection is recorded explicitly', async () => {
    const f = await fixture(); const error = new ProtocolError('contact_deleted', 'Local grant is no longer active');
    await expect(f.repo.accept(relayA, f.entry, f.prepared, true, { queries: [], plan: () => { throw error; } })).rejects.toBe(error);
    expect((await f.repo.getProgress(relayA)).sequence).toBe(-1); expect(await f.repo.readTimeline(0, 10)).toEqual([]);
    expect(await f.repo.reject(relayA, 7, error, true)).toBe(true); expect(await f.repo.reject(relayA, 7, error, false)).toBe(false);
    expect(await f.repo.getProgress(relayA)).toMatchObject({ sequence: 7, hasRetentionGap: true });
    const rows = (await f.store.read([{ collection: 'message_rejections' }])).sets[0]!;
    expect(rows).toHaveLength(1); expect(rows[0]!.value.code).toBe('contact_deleted'); expect(JSON.stringify(rows)).not.toContain(direct.body.text);
});

test('storage failures do not advance synchronization or partially apply message effects', async () => {
    const f = await fixture(); const commit = f.store.commit.bind(f.store); let fail = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (...args) => { if (fail) throw new Error('disk full'); return commit(...args); });
    await expect(f.repo.accept(relayA, f.entry, f.prepared, false)).rejects.toThrow('disk full'); expect((await f.repo.getProgress(relayA)).sequence).toBe(-1);
    fail = false; await f.repo.accept(relayA, f.entry, f.prepared, false); expect((await f.repo.readTimeline(0, 10))[0]!.localSequence).toBe(1);
});

test('deduplication across relays advances each cursor without replaying state changes', async () => {
    const f = await fixture(); const plan = vi.fn(() => []);
    await f.repo.accept(relayA, f.entry, f.prepared, false, { queries: [], plan });
    expect(await f.repo.accept(relayB, { ...f.entry, sequence: 3 }, f.prepared, true, { queries: [], plan })).toEqual({ advanced: true, inserted: false });
    expect(await f.repo.accept(relayA, f.entry, f.prepared, false, { queries: [], plan })).toEqual({ advanced: false, inserted: false });
    expect(plan).toHaveBeenCalledTimes(1); expect(await f.repo.readTimeline(0, 10)).toHaveLength(1);
    expect(await f.repo.getProgress(relayB)).toMatchObject({ sequence: 3, hasRetentionGap: true });
    await f.repo.acceptEmptyPage(relayB, false); expect((await f.repo.getProgress(relayB)).hasRetentionGap).toBe(true);
    await f.repo.observeRelay(relayB); expect((await f.repo.getProgress(relayB)).sequence).toBe(3);
});

test('group synchronization secrets are protected at rest and buffers are erased on success and failure', async () => {
    const f = await fixture(); const payload = { $type: 'meshline.account.group.state.sync', future_secret: 'never store in plaintext' };
    const prepared = await f.repo.prepare(f.entry.envelope, payload); expect(prepared.payload).toBeUndefined(); expect(prepared.protectedPayload).toBeTypeOf('string');
    await f.repo.accept(relayA, f.entry, prepared, false);
    const rows = (await f.store.read([{ collection: 'messages' }])).sets[0]!; expect(JSON.stringify(rows)).not.toContain(payload.future_secret);
    expect((await f.repo.readTimeline(0, 10))[0]!.payload).toEqual(payload); expect(await f.repo.get({ sender: from.certificate.account, messageId: id })).toBeUndefined();
    expect(f.protector.inputs.every(bytes => bytes.every(value => value === 0))).toBe(true); expect(f.protector.plaintexts.every(bytes => bytes.every(value => value === 0))).toBe(true);
    expect(f.protector.purposes[0]).toBe(`Meshline/${context}/${to.certificate.account}/${to.id}/messages/${from.certificate.account}/${id}`);
    f.protector.fail = true; await expect(f.repo.prepare(f.entry.envelope, payload)).rejects.toThrow('keychain unavailable'); expect(f.protector.inputs.at(-1)!.every(value => value === 0)).toBe(true);
    const wrongDevice = new MessageRepository({ ...f.options, deviceId: () => from.id }); await expect(wrongDevice.readTimeline(0, 10)).rejects.toThrow('authentication');
});

test('missing secret protection fails before any timeline state is committed', async () => {
    const f = await fixture(); const { secretProtector: _unused, ...options } = f.options; const repo = new MessageRepository(options);
    await expect(repo.prepare(f.entry.envelope, { $type: 'meshline.account.group.history_secret.sync', secret: 'never plaintext' })).rejects.toThrow('protector');
    expect(await repo.readTimeline(0, 10)).toEqual([]); expect((await repo.getProgress(relayA)).sequence).toBe(-1);
});

test('history readers hold a stable snapshot while the local timeline uses monotonically assigned sequence', async () => {
    const f = await fixture(); await f.repo.accept(relayA, f.entry, f.prepared, false); const reader = await f.repo.history(from.certificate.account);
    const nextId = 'msg_EBESExQVFhcYGRobHB0eHw'; const envelope = { ...f.entry.envelope, messageId: nextId, createdAt: f.clock.wall - 10 };
    await f.repo.accept(relayA, { ...f.entry, envelope, sequence: 9 }, await f.repo.prepare(envelope, direct), false);
    expect((await reader.readNext(10)).map(value => value.key.messageId)).toEqual([id]); expect(await reader.readNext(10)).toEqual([]); await reader.dispose(); await expect(reader.readNext(1)).rejects.toThrow('disposed');
    const latest = await f.repo.history(); expect((await latest.readNext(10)).map(value => value.key.messageId)).toEqual([nextId, id]); await latest.dispose();
    expect((await f.repo.readTimeline(1, 10)).map(value => [value.localSequence, value.messageId])).toEqual([[2, nextId]]);
});

test('outgoing history and outbox insertion are atomic and guarded against changes made during signing', async () => {
    const f = await fixture(); const repo = new MessageRepository({ ...f.options, accountId: from.certificate.account, deviceId: () => from.id });
    const outbox: OutboxRecord = { request: f.request, state: 'queued', relayId: relayA, nextAttemptAt: f.clock.wall, isDirect: true };
    const guard = { collection: 'test_authorization', key: 'peer' }; const snapshot = await f.store.read([]); await f.store.commit(snapshot.version, [{ kind: 'put', ...guard, value: { revision: 2 } }]);
    await expect(repo.enqueue(outbox, f.prepared, [{ query: guard, value: { revision: 1 } }])).rejects.toBeInstanceOf(StateConflictError);
    expect((await f.store.read([{ collection: 'message_outbox' }, messageKey(f.prepared)])).sets).toEqual([[], []]);
    await repo.enqueue(outbox, f.prepared, [{ query: guard, value: { revision: 2 } }]);
    const queue = new MessageOutbox(f.store, f.clock, { getHome: async () => relayA, currentHome: () => relayA, prepare: async () => { throw new Error('unused'); } });
    expect((await queue.get(id))!.state).toBe('queued'); expect((await repo.readTimeline(0, 10))[0]!.localSequence).toBe(1);
    await expect(repo.enqueue(outbox, f.prepared, [])).rejects.toThrow('already exists');
});

test('CAS contention rechecks state guards and cannot create duplicate local sequence numbers', async () => {
    const f = await fixture(); const commit = f.store.commit.bind(f.store); let conflict = true;
    vi.spyOn(f.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (conflict) { conflict = false; await commit(version, [{ kind: 'put', collection: 'unrelated', key: 'x', value: {} }]); throw new StateConflictError('concurrent transaction'); }
        return commit(version, mutations, signal);
    });
    await f.repo.accept(relayA, f.entry, f.prepared, false); expect((await f.repo.readTimeline(0, 10)).map(value => value.localSequence)).toEqual([1]);
    expect(certificateId(to.certificate, context)).toBe(to.id);
});
