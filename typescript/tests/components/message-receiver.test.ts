import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import { MessageKeyAccessError, ProtocolError, StateConflictError, createIdentifier, encryptMessage, messageTimelinePageCodec,
    type MeshlineStore, type MessageDecryptor, type MessageTimelineEntry, type JsonObject } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { MessageRepository } from '../../packages/sdk/dist/messages/repository.js';
import { MessageReceiver, type MessageReceptionPolicy, type ReceptionResult } from '../../packages/sdk/dist/messages/receiver.js';
import { messageDevice } from '../support/message-fixture.js';
import { context } from '../support/relay-fixture.js';
import { AdvancingClock } from '../support/clock.js';
import { removeTestDirectory } from '../support/temp.js';

const directories: string[] = []; const stores: MeshlineStore[] = [];
afterEach(async () => { for (const store of stores.splice(0)) await store.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
const from = messageDevice(context, 1, 2); const to = messageDevice(context, 3, 4); const relay = `0x${'1'.repeat(40)}`;
const direct = { $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: 'receive persist 中文😀' } };
async function fixture(payloads: JsonObject[] = [direct, direct]) {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-receiver-')); directories.push(directory); const store = new NodeSqliteStore(join(directory, 'state.sqlite')); stores.push(store);
    await store.migrate(); await store.initialize({ context: context.toString(), accountId: to.certificate.account }); const clock = new AdvancingClock();
    const repository = new MessageRepository({ store, context, accountId: to.certificate.account, deviceId: () => to.id, clock });
    const entries: MessageTimelineEntry[] = [];
    for (const payload of payloads) {
        const request = await encryptMessage({ context, signer: from, messageId: createIdentifier('message'), createdAt: clock.wall, recipient: to.certificate.account, recipientDevices: [to.certificate], payload });
        entries.push({ sequence: entries.length * 2 + 1, envelope: request.envelope, keyBox: request.recipientBoxes[0]!, acceptedAt: clock.wall });
    }
    const policy: MessageReceptionPolicy = { prepare: async () => undefined, isPermanentRejection: (error): error is ProtocolError => error instanceof ProtocolError && error.code === 'contact_deleted' };
    const device: MessageDecryptor = { ...to }; const receiver = new MessageReceiver(repository, context, device, policy); const results: ReceptionResult[] = [];
    const cursors: number[] = []; const source = { async read(after: number) { cursors.push(after); return messageTimelinePageCodec.encode({ items: entries.filter(value => value.sequence > after), certificates: [from.certificate], hasMore: false, hasRetentionGap: true }); } };
    return { store, clock, repository, entries, policy, device, receiver, results, source, cursors, sync: () => receiver.synchronize(relay, source, value => { results.push(value); }) };
}

test('verified messages are received once; explicit retention gaps persist with the cursor', async () => {
    const f = await fixture(); await f.sync(); expect(f.results[0]!.messages).toHaveLength(2); expect(f.results[0]!.inserted).toBe(2);
    expect(f.results[0]!.messages.map(value => value.localSequence)).toEqual([1, 2]);
    for (const message of f.results[0]!.messages) expect((await f.repository.get(message.key))!.localSequence).toBe(message.localSequence);
    const history = await f.repository.history();
    try { expect((await history.readNext(10)).map(value => value.localSequence)).toEqual([1, 2]); } finally { await history.dispose(); }
    expect(await f.repository.getProgress(relay)).toMatchObject({ sequence: 3, hasRetentionGap: true });
    await f.sync(); expect(f.cursors).toEqual([-1, 3]); expect(f.results).toHaveLength(1); expect(await f.repository.readTimeline(0, 10)).toHaveLength(2);
});

test('malformed envelopes are visibly rejected without blocking later valid messages', async () => {
    const f = await fixture(); f.entries[0] = { ...f.entries[0]!, envelope: { ...f.entries[0]!.envelope, deviceSignature: new Uint8Array(64) } };
    await f.sync(); expect(f.results[0]!.rejected[0]!.error.code).toBe('invalid_signature'); expect(f.results[0]!.messages).toHaveLength(1);
    expect((await f.repository.getProgress(relay)).sequence).toBe(3); expect(await f.repository.readTimeline(0, 10)).toHaveLength(1);
});

test('invalid plaintext business schemas are rejected after authenticated decryption', async () => {
    const f = await fixture([{ $type: 'meshline.message.direct', body: { content_type: 'text/plain', text: '\u0085' } }, direct]);
    await f.sync(); expect(f.results[0]!.rejected[0]!.error.code).toBe('invalid_body'); expect(f.results[0]!.messages).toHaveLength(1);
});

test('local protected-key failures cannot be mistaken for malicious ciphertext and skipped', async () => {
    const f = await fixture(); const keyFailure = new ProtocolError('invalid_ciphertext', 'Local keychain decryption failed');
    f.device.deriveSharedSecret = async () => { throw keyFailure; };
    await expect(f.sync()).rejects.toBeInstanceOf(MessageKeyAccessError); expect((await f.repository.getProgress(relay)).sequence).toBe(-1);
    expect(f.results).toEqual([]); expect((await f.store.read([{ collection: 'message_rejections' }])).sets[0]).toEqual([]);
    f.device.deriveSharedSecret = to.deriveSharedSecret; await f.sync(); expect(f.results[0]!.messages).toHaveLength(2);
});

test('transient failure on a later entry preserves prior commits and emits their notification', async () => {
    const f = await fixture(); let attempts = 0;
    f.policy.prepare = async () => { if (++attempts === 2) throw new Error('remote device lookup timed out'); return undefined; };
    await expect(f.sync()).rejects.toThrow('timed out'); expect((await f.repository.getProgress(relay)).sequence).toBe(1);
    expect(f.results[0]!.messages).toHaveLength(1); expect(f.results[0]!.rejected).toEqual([]);
    await f.sync(); expect(f.cursors).toEqual([-1, 1]); expect(f.results[1]!.messages).toHaveLength(1); expect(await f.repository.readTimeline(0, 10)).toHaveLength(2);
});

test('a revoked local grant discovered inside the transaction becomes a durable business rejection', async () => {
    const f = await fixture([direct]);
    f.policy.prepare = async () => ({ queries: [], plan() { throw new ProtocolError('contact_deleted', 'Grant was removed before commit'); } });
    await f.sync(); expect(f.results[0]!.rejected[0]!.error.code).toBe('contact_deleted'); expect((await f.repository.getProgress(relay)).sequence).toBe(1);
    expect(await f.repository.readTimeline(0, 10)).toEqual([]);
});

test('local CAS conflicts and missing secret protectors leave the cursor retryable', async () => {
    const f = await fixture([direct]); f.policy.prepare = async () => ({ queries: [], plan() { throw new StateConflictError('authorization snapshot changed'); } });
    await expect(f.sync()).rejects.toThrow('snapshot changed'); expect((await f.repository.getProgress(relay)).sequence).toBe(-1);
    const secret = await fixture([{ $type: 'meshline.account.group.state.sync', secret: 'private material' }]);
    await expect(secret.sync()).rejects.toThrow('protector'); expect((await secret.repository.getProgress(relay)).sequence).toBe(-1);
    expect((await secret.store.read([{ collection: 'message_rejections' }])).sets[0]).toEqual([]);
});

test('known messages on another relay advance its cursor without reopening protected device keys', async () => {
    const f = await fixture(); await f.sync(); f.device.deriveSharedSecret = async () => { throw new Error('keys unavailable'); };
    await f.receiver.synchronize(`0x${'2'.repeat(40)}`, f.source, value => { f.results.push(value); });
    expect(f.results).toHaveLength(1); expect((await f.repository.getProgress(`0x${'2'.repeat(40)}`)).sequence).toBe(3);
});

test('page structure is validated before applying any entry', async () => {
    const f = await fixture(); f.entries.reverse();
    await expect(f.sync()).rejects.toThrow('strictly'); expect((await f.repository.getProgress(relay)).sequence).toBe(-1); expect(await f.repository.readTimeline(0, 10)).toEqual([]);
});
