import { afterEach, expect, test } from 'vitest';
import { canonicalJson, groupApplicationInput, type GroupApplication, type QueryReader } from '@meshline/sdk';
import { GroupNetwork } from '../support/group-network.js';
import { messageDevice } from '../support/message-fixture.js';
import { context } from '../support/relay-fixture.js';
const networks: GroupNetwork[] = []; afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
async function all<T>(query: Promise<QueryReader<T>>): Promise<readonly T[]> { const reader = await query; try { return await reader.readNext(100); } finally { await reader.dispose(); } }
async function fixture() {
    const network = new GroupNetwork(); networks.push(network); const owner = await network.client(71); const peer = await network.client(72);
    const group = (await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'Rotation', memberCapacity: 10 })).ref;
    const invite = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, invitee: peer.accountId }); await peer.groups.applyToGroup(invite); await owner.groups.approveApplications(group, [peer.accountId]); await peer.groups.getGroup(group);
    return { network, owner, peer, group };
}
test.each([false, true])('rotation with owner-key replacement=%s activates only after verified commit and all members decrypt the next epoch', async rotateOwnerMemberKey => {
    const { network, owner, peer, group } = await fixture(); await owner.groups.setNickname(group, 'owner nickname'); const before = network.groups.get(group.groupId)!.state;
    let staged = false;
    network.afterPrepare = async () => {
        const rows = (await owner.store.read([{ collection: 'group_rotations' }, { collection: 'group_operations' }])).sets;
        expect(rows[0]).toHaveLength(1); expect(typeof rows[0]![0]!.value.protectedSecret).toBe('string'); expect(rows[1]).toEqual([]);
        expect(network.groups.get(group.groupId)!.state.clientSecretCommitment).toBe(before.clientSecretCommitment);
        expect((await all(owner.groups.getMembers(group))).find(value => value.accountId === owner.accountId)!.memberEncryptionPublicKey).toEqual(before.members.find(value => value.account === owner.accountId)!.memberEncryptionPublicKey); staged = true;
    };
    await owner.groups.rotateSecret(group, { rotateOwnerMemberKey }); expect(staged).toBe(true);
    const after = network.groups.get(group.groupId)!.state; expect(after.epoch).toBe(before.epoch + 1); expect(after.clientSecretCommitment).not.toBe(before.clientSecretCommitment);
    const oldKey = before.members.find(value => value.account === owner.accountId)!.memberEncryptionPublicKey; const newKey = after.members.find(value => value.account === owner.accountId)!.memberEncryptionPublicKey;
    expect(canonicalJson([...oldKey]) === canonicalJson([...newKey])).toBe(!rotateOwnerMemberKey);
    expect((await all(owner.groups.getMembers(group))).find(value => value.accountId === owner.accountId)!.nickname).toBe('owner nickname');
    expect((await owner.store.read([{ collection: 'group_rotations' }, { collection: 'group_operations' }])).sets).toEqual([[], []]);
    const sent = await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'new client secret' } }); await peer.groups.getGroup(group); expect((await all(peer.groups.getMessages())).at(-1)!.messageId).toBe(sent.messageId);
});

test('lost prepare acknowledgement resumes the protected candidate and owner key after restart', async () => {
    const { network, owner, peer, group } = await fixture(); network.loseResponse = 'group.secret.rotation.prepare';
    await expect(owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true })).rejects.toThrow('response lost');
    const staged = (await owner.store.read([{ collection: 'group_rotations' }])).sets[0]![0]!.value; expect(staged.expiresAt).toBeUndefined();
    const before = network.groups.get(group.groupId)!.state.clientSecretCommitment; await owner.dispose(); network.loseResponse = undefined;
    const resumed = await network.client(71, owner.path); await resumed.groups.rotateSecret(group, { rotateOwnerMemberKey: true });
    const requests = network.requests.filter(value => value.method === 'group.secret.rotation.prepare'); expect(requests).toHaveLength(2); expect(requests.every(value => value.body.client_secret_commitment === staged.commitment)).toBe(true);
    expect(network.groups.get(group.groupId)!.state.clientSecretCommitment).not.toBe(before); await peer.groups.getGroup(group); const sent = await peer.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'resumed rotation works' } }); await resumed.groups.getGroup(group); expect((await all(resumed.groups.getMessages()))[0]!.messageId).toBe(sent.messageId);
});

test.each([false, true])('uncertain rotation commit accepted=%s recovers exact signed content without recreating preparation', async accepted => {
    const { network, owner, peer, group } = await fixture(); if (accepted) network.loseResponse = 'group.secret.rotation.commit'; else network.failBeforeAccept = 'group.secret.rotation.commit';
    await expect(owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true })).rejects.toThrow(); const pending = (await owner.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value.request;
    await owner.dispose(); network.loseResponse = undefined; network.failBeforeAccept = undefined; const resumed = await network.client(71, owner.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    expect((await resumed.store.read([{ collection: 'group_rotations' }])).sets[0]).toEqual([]); expect(network.requests.filter(value => value.method === 'group.secret.rotation.prepare')).toHaveLength(1);
    const commits = network.requests.filter(value => value.method === 'group.secret.rotation.commit'); expect(commits).toHaveLength(accepted ? 1 : 2); expect(commits.every(value => canonicalJson(value.body) === canonicalJson(pending!))).toBe(true);
    await peer.groups.getGroup(group); await resumed.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'confirmed rotation' } }); await peer.groups.getGroup(group); expect((await all(peer.groups.getMessages()))[0]!.body!.text).toBe('confirmed rotation');
});

test('membership changes preserve the candidate secret while a retry uses only currently verified members', async () => {
    const { network, owner, peer, group } = await fixture(); network.afterPrepare = async () => { network.afterPrepare = undefined; await peer.groups.leaveGroup(group); };
    await expect(owner.groups.rotateSecret(group)).rejects.toThrow('state_conflict');
    const stage = (await owner.store.read([{ collection: 'group_rotations' }])).sets[0]![0]!.value; expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual([]);
    await owner.groups.rotateSecret(group); const requests = network.requests.filter(value => value.method === 'group.secret.rotation.prepare'); expect(requests).toHaveLength(2); expect(requests[1]!.body.client_secret_commitment).toBe(stage.commitment); expect(Object.keys(requests[1]!.body.client_secret_boxes!)).toEqual([owner.accountId]);
    expect((await all(peer.groups.getGroups()))[0]!.membership).toBe('left');
});

test('expired preparations require a fresh secret and fresh owner candidate key', async () => {
    const { network, owner, group } = await fixture(); network.rejectMethod = 'group.secret.rotation.commit';
    await expect(owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true })).rejects.toThrow('state_conflict'); const old = (await owner.store.read([{ collection: 'group_rotations' }])).sets[0]![0]!.value;
    network.network.clock.wall += 301; network.rejectMethod = undefined; await owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true });
    const requests = network.requests.filter(value => value.method === 'group.secret.rotation.prepare'); expect(requests[1]!.body.client_secret_commitment).not.toBe(old.commitment);
    const commit = network.requests.filter(value => value.method === 'group.secret.rotation.commit').at(-1)!.body; expect(commit.owner_encryption_public_key).not.toBe(old.ownerPublicKey);
});

test('ownership transfer away and back invalidates an old candidate even when the base commitment is unchanged', async () => {
    const { network, owner, peer, group } = await fixture(); network.rejectMethod = 'group.secret.rotation.commit'; await expect(owner.groups.rotateSecret(group)).rejects.toThrow('state_conflict');
    const old = (await owner.store.read([{ collection: 'group_rotations' }])).sets[0]![0]!.value; network.rejectMethod = undefined;
    await owner.groups.transferOwnership(group, peer.accountId); await peer.groups.transferOwnership(group, owner.accountId); await owner.groups.rotateSecret(group);
    expect(network.requests.filter(value => value.method === 'group.secret.rotation.prepare').at(-1)!.body.client_secret_commitment).not.toBe(old.commitment);
});

test('a staged rotation preserves the owner-key choice and original preparation expiry', async () => {
    const { network, owner, group } = await fixture(); network.rejectMethod = 'group.secret.rotation.commit'; await expect(owner.groups.rotateSecret(group)).rejects.toThrow('state_conflict');
    const old = (await owner.store.read([{ collection: 'group_rotations' }])).sets[0]![0]!.value;
    await expect(owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true })).rejects.toThrow('original owner-key choice');
    network.prepareResponse = value => ({ ...value, expiresAt: value.expiresAt + 1 }); await expect(owner.groups.rotateSecret(group)).rejects.toThrow('original rotation preparation expiry');
    expect((await owner.store.read([{ collection: 'group_rotations' }])).sets[0]![0]!.value.expiresAt).toBe(old.expiresAt);
    network.prepareResponse = undefined; network.rejectMethod = undefined; await owner.groups.rotateSecret(group); expect(network.groups.get(group.groupId)!.state.clientSecretCommitment).toBe(old.commitment);
});

test('only the verified current owner can stage rotation boxes', async () => {
    const { network, peer, group } = await fixture(); await expect(peer.groups.rotateSecret(group)).rejects.toThrow('current owner'); expect(network.requests.filter(value => value.method === 'group.secret.rotation.prepare')).toEqual([]);
    expect((await peer.store.read([{ collection: 'group_rotations' }])).sets[0]).toEqual([]);
});

async function largeFixture() {
    const { network, owner, group } = await fixture(); await owner.groups.updateGroup(group, { memberCapacity: 100 });
    const invite = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, maxUses: 63 }); const accounts: string[] = [];
    for (let seed = 100; seed < 163; seed++) {
        const device = messageDevice(context, seed, seed + 1); const account = device.certificate.account; accounts.push(account);
        let application: GroupApplication = { groupId: group.groupId, inviteId: invite.document.inviteId, account, memberEncryptionPublicKey: device.certificate.encryptionPublicKey, deviceSignature: new Uint8Array(64) };
        application = { ...application, deviceSignature: await device.sign(groupApplicationInput(application, context)) };
        network.applications.set(`${group.groupId}|${account}`, { application, signerCertificate: device.certificate, acceptedAt: network.network.clock.wall });
    }
    await owner.groups.approveApplications(group, accounts); expect(network.groups.get(group.groupId)!.state.members).toHaveLength(65);
    return { network, owner, group };
}

test('rotation prepares 65 verified members in two batches under one candidate and expiry', async () => {
    const { network, owner, group } = await largeFixture();
    const expiries: number[] = []; network.prepareResponse = value => { expiries.push(value.expiresAt); return value; };
    await owner.groups.rotateSecret(group);
    const batches = network.requests.filter(value => value.method === 'group.secret.rotation.prepare');
    expect(batches.map(value => Object.keys(value.body.client_secret_boxes!).length)).toEqual([64, 1]);
    expect(new Set(batches.map(value => value.body.client_secret_commitment)).size).toBe(1); expect(new Set(expiries).size).toBe(1);
    expect((await owner.store.read([{ collection: 'group_rotations' }, { collection: 'group_operations' }])).sets).toEqual([[], []]);
});

test.each(['expired', 'count', 'changed_interval'] as const)('an invalid %s preparation response cannot publish a rotation commit', async scenario => {
    const { network, owner, group } = scenario === 'changed_interval' ? await largeFixture() : await fixture();
    const before = network.groups.get(group.groupId)!.state;
    const originalExpiry = network.network.clock.wall + 300; let batches = 0;
    network.prepareResponse = value => {
        batches++;
        return { ...value, prepared: scenario === 'count' ? 0 : value.prepared,
            expiresAt: scenario === 'expired' ? network.network.clock.wall : originalExpiry + (scenario === 'changed_interval' ? batches - 1 : 0) };
    };
    await expect(owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true })).rejects.toThrow();
    const requests = network.requests.filter(value => value.method === 'group.secret.rotation.prepare');
    expect(requests.map(value => Object.keys(value.body.client_secret_boxes!).length)).toEqual(scenario === 'changed_interval' ? [64, 1] : [2]);
    expect(network.requests.filter(value => value.method === 'group.secret.rotation.commit')).toEqual([]);
    const rows = (await owner.store.read([{ collection: 'group_rotations' }, { collection: 'group_operations' }])).sets;
    expect(rows[0]).toHaveLength(1); expect(rows[1]).toEqual([]);
    expect(rows[0]![0]!.value.expiresAt).toBe(scenario === 'changed_interval' ? originalExpiry : undefined);
    expect(network.groups.get(group.groupId)!.state).toEqual(before);
    // The candidate survives a bad response and remains usable with a valid retry.
    const candidate = rows[0]![0]!.value; network.prepareResponse = undefined;
    await owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true });
    const committed = network.requests.filter(value => value.method === 'group.secret.rotation.commit');
    expect(committed).toHaveLength(1);
    expect(committed[0]!.body).toMatchObject({ client_secret_commitment: candidate.commitment, owner_encryption_public_key: candidate.ownerPublicKey });
});
