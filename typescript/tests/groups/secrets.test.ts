import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import { NodeSqliteStore } from '@meshline/storage-node';
import {
    NetworkContext, canonicalJson, decodeBase64Url, decryptAes, deriveGroupApplicationSecret, deriveResourceId, encodeBase64Url, encodeUtf8, encryptAes,
    encryptionPublicKey, groupClientSecretBoxAad, groupClientSecretCommitment, groupCreateCodec, groupCreateInput, groupManagementInput, groupManagementPayloadCodec,
    groupRelaySecretBoxAad, sealGroupClientSecret, sealGroupSecret, signDevice, systemRandom,
    type GroupCreate, type GroupEvent, type GroupKeyEntry, type GroupSecretRotation, type SecretProtector,
} from '@meshline/sdk';
import { GroupRepository } from '../../packages/sdk/dist/groups/repository.js';
import { GroupSecrets } from '../../packages/sdk/dist/groups/secrets.js';
import { messageDevice } from '../support/message-fixture.js';
import { removeTestDirectory } from '../support/temp.js';
const context = NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
const resources: NodeSqliteStore[] = []; const directories: string[] = [];
afterEach(async () => { for (const store of resources.splice(0)) await store.dispose(); for (const directory of directories.splice(0)) await removeTestDirectory(directory); });
async function fixture(establish = true) {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-group-secrets-')); directories.push(directory); const path = join(directory, 'state.sqlite');
    const device = messageDevice(context, 20, 21); const accountId = device.certificate.account; const store = new NodeSqliteStore(path); resources.push(store); await store.migrate(); await store.initialize({ context: context.toString(), accountId });
    const nonce = new Uint8Array(16).fill(30); const relayId = '0x' + '7'.repeat(40); const group = { groupId: deriveResourceId('group', accountId, relayId, nonce, context), relayId };
    const memberKeys = [new Uint8Array(32).fill(71), new Uint8Array(32).fill(72)]; const clientSecrets = [new Uint8Array(32).fill(91), new Uint8Array(32).fill(92)];
    const epochs = [0, 2, 4]; const relaySecrets = epochs.map((_, i) => new Uint8Array(32).fill(101 + i)); const commitments = clientSecrets.map(secret => groupClientSecretCommitment(group.groupId, secret, context));
    const master = new Uint8Array(32).fill(111); const protectedInputs: Uint8Array[] = []; const restored: Uint8Array[] = []; let locked = false; let failProtectionAt = 0; let protections = 0;
    const protector: SecretProtector = {
        async protect(plaintext, purpose) { protectedInputs.push(plaintext); if (failProtectionAt && ++protections === failProtectionAt) throw new Error('OS protection unavailable'); const iv = systemRandom.bytes(12); return new Uint8Array([...iv, ...encryptAes(master, iv, plaintext, encodeUtf8(purpose))]); },
        async unprotect(ciphertext, purpose) { if (locked) throw new Error('OS keys locked'); const key = decryptAes(master, ciphertext.subarray(0, 12), ciphertext.subarray(12), encodeUtf8(purpose)); restored.push(key); return key; },
    };
    const options = { store, context, accountId, deviceId: () => device.id, protector }; const vault = new GroupSecrets(options); const repository = new GroupRepository(store, context, accountId);
    let create: GroupCreate = { groupId: group.groupId, nonce, name: 'Protected test group', memberCapacity: 10, invitePolicy: 'administrators', owner: { account: accountId, memberEncryptionPublicKey: encryptionPublicKey(memberKeys[0]!) }, clientSecretCommitment: commitments[0]!, deviceSignature: new Uint8Array(64) };
    create = { ...create, deviceSignature: signDevice(groupCreateInput(create, context), device.signingKey) };
    const timeline: GroupEvent[] = [{ sequence: 0, epoch: 0, payload: groupCreateCodec.encode(create), signerDeviceId: device.id, acceptedAt: 1730000000 }, { sequence: 1, epoch: 2, payload: { $type: 'meshline.group.key.rotated' }, acceptedAt: 1730000001 }];
    const sync = () => repository.synchronizePage(group, { async read(query) { return { events: timeline.filter(entry => entry.sequence > (query.after ?? -1)), certificates: [device.certificate], hasMore: false }; } });
    const establishHistory = async () => {
        await sync(); const state = (await repository.get(group))!;
        let rotation: { kind: 'secretRotation'; value: GroupSecretRotation } = { kind: 'secretRotation', value: { groupId: group.groupId, prevHash: state.managementHash, clientSecretCommitment: commitments[1]!, ownerEncryptionPublicKey: encryptionPublicKey(memberKeys[1]!), deviceSignature: new Uint8Array(64) } };
        rotation = { ...rotation, value: { ...rotation.value, deviceSignature: signDevice(groupManagementInput(rotation, context), device.signingKey) } };
        timeline.push({ sequence: 2, epoch: 4, payload: groupManagementPayloadCodec.encode(rotation) as ReturnType<typeof groupCreateCodec.encode>, signerDeviceId: device.id, acceptedAt: 1730000002 }); await sync();
    };
    const keys: GroupKeyEntry[] = epochs.map((epoch, i) => {
        const current = i === 2 ? 1 : 0; const header = { groupId: group.groupId, account: accountId, memberEncryptionPublicKey: encryptionPublicKey(memberKeys[current]!), clientSecretCommitment: commitments[current]! };
        return { epoch, ...(i === 1 ? {} : { clientSecretBox: sealGroupClientSecret(header, clientSecrets[current]!, context) }),
            relaySecretBox: sealGroupSecret(relaySecrets[i]!, device.certificate.encryptionPublicKey, groupRelaySecretBoxAad({ groupId: group.groupId, account: accountId, deviceId: device.id, epoch }, context)) };
    });
    const applicationSecrets = epochs.map((epoch, i) => deriveGroupApplicationSecret(group.groupId, epoch, commitments[i === 2 ? 1 : 0]!, clientSecrets[i === 2 ? 1 : 0]!, relaySecrets[i]!, context));
    if (establish) await establishHistory();
    const saveMember = (index: number) => vault.saveMemberState({ ...group, memberEncryptionPrivateKey: memberKeys[index]! });
    return { store, path, accountId, device, group, memberKeys, clientSecrets, commitments, applicationSecrets, keys, vault, repository, options, saveMember, establishHistory, protectedInputs, restored,
        set locked(value: boolean) { locked = value; }, set failProtectionAt(value: number) { failProtectionAt = value; protections = 0; } };
}
test('future account-synchronized secrets remain staged until verified history establishes the exact epoch', async () => {
    const f = await fixture(false); await f.vault.stageHistorySecret({ groupId: f.group.groupId, epoch: 4, applicationSecret: f.applicationSecrets[2]! });
    expect(await f.vault.readApplicationSecret(f.group, 4)).toBeUndefined(); expect(await f.repository.get(f.group)).toBeUndefined();
    await f.establishHistory(); const secret = await f.vault.readApplicationSecret(f.group, 4); expect(secret).toEqual(f.applicationSecrets[2]); secret!.fill(0);
    await f.vault.stageHistorySecret({ groupId: f.group.groupId, epoch: 3, applicationSecret: f.applicationSecrets[2]! }); expect(await f.vault.readApplicationSecret(f.group, 3)).toBeUndefined();
    expect((await f.repository.get(f.group))!.sequence).toBe(2);
});
test('missing old member keys do not block the current epoch; later old-key arrival never replaces the current public-key selection', async () => {
    const f = await fixture(); await f.saveMember(1); await f.repository.saveKeyPage(f.group, { keys: f.keys, hasMore: false }, -1);
    const first = await f.vault.derivePending(f.group, f.device); expect(first).toEqual({ derived: [4], missing: [0, 2], rejected: [] });
    expect(await f.vault.readApplicationSecret(f.group, 0)).toBeUndefined(); const current = await f.vault.readApplicationSecret(f.group, 4); expect(current).toEqual(f.applicationSecrets[2]); current!.fill(0);
    await f.saveMember(0); const second = await f.vault.derivePending(f.group, f.device); expect(second).toEqual({ derived: [0, 2], missing: [], rejected: [] });
    const currentPublicKey = (await f.repository.get(f.group))!.members[0]!.memberEncryptionPublicKey; const privateKey = await f.vault.readMemberKey(f.group.groupId, currentPublicKey); expect(privateKey).toEqual(f.memberKeys[1]); privateKey!.fill(0);
    expect(f.protectedInputs.every(input => input.every(byte => byte === 0))).toBe(true); expect(f.restored.every(input => input.every(byte => byte === 0))).toBe(true);
});
test('member and application secrets survive restart with purpose binding and no raw secrets in stored records', async () => {
    const f = await fixture(); await f.saveMember(0); await f.saveMember(1); await f.repository.saveKeyPage(f.group, { keys: f.keys, hasMore: false }, -1); await f.vault.derivePending(f.group, f.device);
    const snapshot = await f.store.read(['group_member_keys', 'group_client_secrets', 'group_application_secrets', 'group_epochs'].map(collection => ({ collection })));
    const serialized = canonicalJson(snapshot.sets.map(rows => rows.map(row => row.value)));
    for (const secret of [...f.memberKeys, ...f.clientSecrets, ...f.applicationSecrets]) expect(serialized).not.toContain(encodeBase64Url(secret));
    await f.store.dispose(); const store = new NodeSqliteStore(f.path); resources.push(store); await store.initialize({ context: context.toString(), accountId: f.accountId }); const vault = new GroupSecrets({ ...f.options, store });
    const secret = await vault.readApplicationSecret(f.group, 4); expect(secret).toEqual(f.applicationSecrets[2]); secret!.fill(0);
    const otherDevice = messageDevice(context, 20, 25); const wrong = new GroupSecrets({ ...f.options, store, deviceId: () => otherDevice.id });
    await expect(wrong.readApplicationSecret(f.group, 4)).rejects.toThrow('could not be restored');
});
test('key-box authentication and commitment failures remain visible while independently valid later epochs recover', async () => {
    const f = await fixture(); await f.saveMember(0); await f.saveMember(1);
    const changed = { ...f.keys[0]!, relaySecretBox: { ...f.keys[0]!.relaySecretBox, sealedSecret: f.keys[0]!.relaySecretBox.sealedSecret.slice() } }; changed.relaySecretBox.sealedSecret[59]! ^= 1;
    await f.repository.saveKeyPage(f.group, { keys: [changed, ...f.keys.slice(1)], hasMore: false }, -1);
    const result = await f.vault.derivePending(f.group, f.device); expect(result.derived).toEqual([2, 4]); expect(result.rejected.map(row => [row.epoch, row.error.code])).toEqual([[0, 'invalid_ciphertext']]); expect(await f.vault.readApplicationSecret(f.group, 0)).toBeUndefined();
    await f.repository.saveKeyPage(f.group, { keys: [f.keys[0]!], hasMore: false }, -1); expect((await f.vault.derivePending(f.group, f.device)).derived).toEqual([0]);
});
test('a decryptable client box with the wrong committed secret cannot activate an epoch', async () => {
    const f = await fixture(); await f.saveMember(1); const header = { groupId: f.group.groupId, account: f.accountId, memberEncryptionPublicKey: encryptionPublicKey(f.memberKeys[1]!), clientSecretCommitment: f.commitments[1]! };
    const clientSecretBox = sealGroupSecret(f.clientSecrets[0]!, header.memberEncryptionPublicKey, groupClientSecretBoxAad(header, context));
    await f.repository.saveKeyPage(f.group, { keys: [{ ...f.keys[2]!, clientSecretBox }], hasMore: false }, -1);
    const result = await f.vault.derivePending(f.group, f.device); expect(result.rejected[0]!.error.code).toBe('invalid_commitment'); expect(await f.vault.readApplicationSecret(f.group, 4)).toBeUndefined();
});
test('conflicting relay material cannot replace a previously synchronized epoch secret', async () => {
    const f = await fixture(); await f.saveMember(1); await f.vault.stageHistorySecret({ groupId: f.group.groupId, epoch: 4, applicationSecret: f.applicationSecrets[2]! });
    const relaySecretBox = sealGroupSecret(new Uint8Array(32).fill(222), f.device.certificate.encryptionPublicKey, groupRelaySecretBoxAad({ groupId: f.group.groupId, account: f.accountId, deviceId: f.device.id, epoch: 4 }, context));
    await f.repository.saveKeyPage(f.group, { keys: [{ ...f.keys[2]!, relaySecretBox }], hasMore: false }, -1);
    expect((await f.vault.derivePending(f.group, f.device)).rejected[0]!.error.code).toBe('conflicting_epoch_secret'); const old = await f.vault.readApplicationSecret(f.group, 4); expect(old).toEqual(f.applicationSecrets[2]); old!.fill(0);
    await expect(f.vault.stageHistorySecret({ groupId: f.group.groupId, epoch: 4, applicationSecret: new Uint8Array(32).fill(33) })).rejects.toThrow('conflicts');
    await f.repository.saveKeyPage(f.group, { keys: [f.keys[2]!], hasMore: false }, -1); expect((await f.vault.derivePending(f.group, f.device)).derived).toEqual([4]);
});
test('local protection failure cannot partially persist client/application secrets or mark the epoch verified', async () => {
    const f = await fixture(); await f.saveMember(1); await f.repository.saveKeyPage(f.group, { keys: [f.keys[2]!], hasMore: false }, -1); f.failProtectionAt = 2;
    await expect(f.vault.derivePending(f.group, f.device)).rejects.toThrow('protection failed');
    expect((await f.store.read([{ collection: 'group_application_secrets' }, { collection: 'group_client_secrets' }])).sets).toEqual([[], []]);
    expect((await f.store.read([{ collection: 'group_epochs', key: `${f.group.groupId}|0000000000000004` }])).sets[0]![0]!.value.materialVerified).toBeUndefined();
    expect(f.protectedInputs.every(input => input.every(byte => byte === 0))).toBe(true); f.failProtectionAt = 0; expect((await f.vault.derivePending(f.group, f.device)).derived).toEqual([4]);
    f.locked = true; await expect(f.vault.readApplicationSecret(f.group, 4)).rejects.toThrow('restored');
});
test('member state cannot rebind an existing group to another hosting relay', async () => {
    const f = await fixture(); await expect(f.vault.saveMemberState({ groupId: f.group.groupId, relayId: '0x' + 'f'.repeat(40), memberEncryptionPrivateKey: f.memberKeys[0]! })).rejects.toThrow('another hosting');
    expect((await f.store.read([{ collection: 'group_member_keys' }])).sets[0]).toEqual([]);
});
