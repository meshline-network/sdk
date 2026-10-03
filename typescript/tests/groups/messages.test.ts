import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import { NodeSqliteStore } from '@meshline/storage-node';
import { ProtocolError, decodeBase64Url, deviceCertificateCodec, encryptGroupMessage, groupEventCodec, groupMessageEnvelopeCodec, signDevice, type GroupEvent, type JsonObject } from '@meshline/sdk';
import { GroupRepository } from '../../packages/sdk/dist/groups/repository.js';
import { GroupMessageProcessor } from '../../packages/sdk/dist/groups/messages.js';
import { groupActors, groupContext as context, groupReference as group, groupVectors, signedGroupEvent } from '../support/group-fixture.js';
import { vector } from '../support/vectors.js';
import { removeTestDirectory } from '../support/temp.js';
const data = vector<{ encrypted_nicknames: { epoch_inputs: { epoch: number; epoch_application_secret: string }[]; messages: { name: string; starting_event_index: number; event: JsonObject; plaintext: JsonObject }[];
    management_preservation: { name: string; starting_event_index: number; nickname_message: string; steps: { event: JsonObject }[]; expected_local_nickname: { account: string; nickname: string; sequence: number } }[] } }>('groups').encrypted_nicknames;
const messages = new Map(data.messages.map(row => [row.name, row])); const resources: NodeSqliteStore[] = []; const directories: string[] = [];
afterEach(async () => { for (const store of resources.splice(0)) await store.dispose(); for (const directory of directories.splice(0)) await removeTestDirectory(directory); });
async function fixture(index = 3) {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-group-messages-')); directories.push(directory); const path = join(directory, 'state.sqlite');
    const accountId = groupActors.get('A')!.certificate.account; const store = new NodeSqliteStore(path); resources.push(store); await store.migrate(); await store.initialize({ context: context.toString(), accountId });
    const repository = new GroupRepository(store, context, accountId);
    const certificates = [...groupActors.values()].map(actor => actor.certificate).concat(groupVectors.management_chain.signer_account_binding.cases.map(row => deviceCertificateCodec.decode(row.certificate)));
    const sync = (events: GroupEvent[]) => repository.synchronizePage(group, { async read(query) { return { events: events.filter(entry => entry.sequence > (query.after ?? -1)), certificates, hasMore: false }; } });
    const initial = groupVectors.management_chain.chain.slice(0, index + 1).map(row => groupEventCodec.decode(row.event)); if (index >= 5) initial.push(groupEventCodec.decode(groupVectors.management_chain.automatic_rotation.event));
    await sync(initial.sort((a, b) => a.sequence - b.sequence));
    let available = true; let failure: Error | undefined; const returnedKeys: Uint8Array[] = [];
    const secrets = { async readApplicationSecret(_group: unknown, epoch: number) {
        if (failure) throw failure; if (!available) return undefined;
        const encoded = data.epoch_inputs.find(row => row.epoch === epoch)?.epoch_application_secret; if (!encoded) return undefined;
        const key = decodeBase64Url(encoded); returnedKeys.push(key); return key;
    } };
    const processor = new GroupMessageProcessor(store, context, accountId, secrets);
    const add = (names: string[]) => sync(names.map(name => groupEventCodec.decode(messages.get(name)!.event)).sort((a, b) => a.sequence - b.sequence));
    const member = async (actor = 'B') => (await repository.get(group))!.members.find(value => value.account === groupActors.get(actor)!.certificate.account);
    return { store, path, repository, processor, accountId, secrets, sync, add, member, returnedKeys, set available(value: boolean) { available = value; }, set failure(value: Error | undefined) { failure = value; } };
}
test('delayed decryption cannot overwrite a newer nickname clear, and plaintext plus display state commit atomically', async () => {
    const f = await fixture(); await f.add(['administrator_set', 'clear']); f.available = false;
    expect((await f.processor.process(group, 30)).state).toBe('waitingForKey'); expect((await f.member())!.nickname).toBeUndefined();
    f.available = true; expect((await f.processor.process(group, 32)).nicknameChanged).toBe(true); expect((await f.member())!.nicknameSequence).toBe(32);
    const processed = await f.processor.process(group, 30); expect(processed.nicknameChanged).toBeUndefined(); expect((await f.member())!.nickname).toBeUndefined();
    expect((await f.processor.process(group, 32)).state).toBe('alreadyProcessed'); expect(f.returnedKeys.every(key => key.every(byte => byte === 0))).toBe(true);
    const snapshot = await f.store.read([{ collection: 'groups' }, { collection: 'group_events', key: `${group.groupId}|0000000000000032` }]);
    expect(snapshot.sets[0]![0]!.revision).toBe(snapshot.sets[1]![0]!.revision); expect(snapshot.sets[1]![0]!.value.decryptedPayload).toEqual(messages.get('clear')!.plaintext);
});
test('nickname target is authenticated sender; claimed accounts, roles and groups in extensions cannot redirect it', async () => {
    const f = await fixture(); await f.add(['owner_set', 'member_set', 'administrator_set', 'owner_other_device', 'unknown_attributes']);
    await f.processor.processPending(group, () => {});
    expect((await f.member('A'))!.nickname).toBe('青山'); expect((await f.member('C'))!.nickname).toBe('晨星'); expect((await f.member())!.nickname).toBe('本人昵称');
    expect((await f.member())!.role).toBe('administrator'); expect((await f.repository.get(group))!.state.owner).toBe(f.accountId);
});
test('invalid and future content retain diagnostics or authenticated payload without changing the nickname', async () => {
    const f = await fixture(); await f.add(['administrator_set', 'invalid_empty', 'unknown_type']);
    const results: string[] = []; await f.processor.processPending(group, (sequence, result) => { if (result.error) results.push(`${sequence}:${result.error.code}`); });
    expect(results.length).toBe(1); expect(results[0]).toContain('invalid_nickname'); expect((await f.member())!.nickname).toBe('阿青');
    const unknown = messages.get('unknown_type')!; const record = await f.repository.event(group, Number(unknown.event.sequence)); expect(record!.decryptedPayload).toEqual(unknown.plaintext); expect(record!.isMessage).toBe(false);
    expect((await f.store.read([{ collection: 'group_pending_messages' }])).sets[0]).toEqual([]);
});
test('local key access failure remains retryable even when the provider uses a protocol error type', async () => {
    const f = await fixture(); await f.add(['administrator_set']); f.failure = new ProtocolError('invalid_ciphertext', 'OS-protected key is locked');
    await expect(f.processor.process(group, 30)).rejects.toThrow('locked'); expect((await f.repository.event(group, 30))!.rejection).toBeUndefined();
    expect((await f.store.read([{ collection: 'group_pending_messages' }])).sets[0]!.length).toBe(1);
    f.failure = undefined; expect((await f.processor.process(group, 30)).nicknameChanged).toBe(true);
});
test('restart resumes pending decryption without re-fetching already verified timeline entries', async () => {
    const f = await fixture(); await f.add(['administrator_set']); f.available = false; await f.processor.processPending(group, () => {}); await f.store.dispose();
    const store = new NodeSqliteStore(f.path); resources.push(store); await store.initialize({ context: context.toString(), accountId: f.accountId });
    f.available = true; const processor = new GroupMessageProcessor(store, context, f.accountId, f.secrets); const completed: number[] = [];
    await processor.processPending(group, sequence => completed.push(sequence)); expect(completed).toEqual([30]);
    const repository = new GroupRepository(store, context, f.accountId); expect((await repository.get(group))!.sequence).toBe(30); expect((await repository.event(group, 30))!.decryptedPayload).toEqual(messages.get('administrator_set')!.plaintext);
});
test('departure and readmission clear nickname state and delayed messages from the previous membership cannot restore it', async () => {
    const f = await fixture(); await f.add(['administrator_set', 'clear']); await f.processor.process(group, 30); const before = (await f.repository.get(group))!;
    await f.sync([signedGroupEvent(before, 'A', 'member.ban', { accounts: [groupActors.get('B')!.certificate.account] }, before.epoch + 1)]); expect(await f.member()).toBeUndefined();
    const banned = (await f.repository.get(group))!; await f.sync([signedGroupEvent(banned, 'A', 'member.unban', { accounts: [groupActors.get('B')!.certificate.account] })]);
    const unbanned = (await f.repository.get(group))!; await f.sync([signedGroupEvent(unbanned, 'A', 'application.approval', { members: [{ account: groupActors.get('B')!.certificate.account, member_encryption_public_key: groupActors.get('B')!.member_encryption_public_key }] }, unbanned.epoch + 1)]);
    expect((await f.processor.process(group, 32)).nicknameChanged).toBeUndefined(); expect((await f.member())!.nicknameSequence).toBeUndefined(); expect((await f.member())!.role).toBe('member');
});
test.each(data.management_preservation)('nickname survives verified management change: $name', async row => {
    const f = await fixture(row.starting_event_index); await f.add([row.nickname_message]); const message = messages.get(row.nickname_message)!;
    await f.processor.process(group, Number(message.event.sequence)); await f.sync(row.steps.map(step => groupEventCodec.decode(step.event)));
    const member = (await f.repository.get(group))!.members.find(value => value.account === row.expected_local_nickname.account)!;
    expect(member.nickname).toBe(row.expected_local_nickname.nickname); expect(member.nicknameSequence).toBe(row.expected_local_nickname.sequence);
});
test('decoded chat returns typed content while a forward reply reference is rejected without losing later work', async () => {
    const f = await fixture(); const actor = groupActors.get('B')!; const secret = decodeBase64Url(data.epoch_inputs.find(value => value.epoch === 4)!.epoch_application_secret);
    const signer = { certificate: actor.certificate, async sign(input: Uint8Array) { return signDevice(input, decodeBase64Url(actor.signing_private_key)); } };
    const entries: GroupEvent[] = [];
    for (const sequence of [30, 31]) {
        const payload = { $type: 'meshline.group.message.content', body: { content_type: 'text/plain', text: `message ${sequence}` }, reply_to_seq: sequence === 30 ? 30 : 1 };
        const envelope = await encryptGroupMessage({ context, signer, groupId: group.groupId, epoch: 4, messageId: sequence === 30 ? 'msg_AAECAwQFBgcICQoLDA0ODw' : 'msg_EBESExQVFhcYGRobHB0eHw', createdAt: 1730000010, applicationSecret: secret, payload });
        entries.push({ sequence, epoch: 4, payload: groupMessageEnvelopeCodec.encode(envelope), signerDeviceId: actor.device_id, acceptedAt: 1730000030 });
    }
    await f.sync(entries); const received: string[] = []; const rejections: string[] = [];
    await f.processor.processPending(group, (_sequence, result) => { if (result.message) received.push(result.message.body!.text); if (result.error) rejections.push(result.error.code); });
    expect(received).toEqual(['message 31']); expect(rejections).toEqual(['invalid_reply']);
});
