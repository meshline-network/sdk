import { afterEach, expect, test } from 'vitest';
import { canonicalJson, encodeBase64Url, groupApplicationCodec, groupApplicationInput, groupMemberRecoveryRequestCodec, ProtocolError, verifyGroupApplication, type GroupInvitation, type QueryReader } from '@meshline/sdk';
import { context } from '../support/relay-fixture.js';
import { GroupNetwork } from '../support/group-network.js';
const networks: GroupNetwork[] = []; afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
async function all<T>(query: Promise<QueryReader<T>>): Promise<readonly T[]> { const reader = await query; try { return await reader.readNext(100); } finally { await reader.dispose(); } }
async function fixture(join = false) {
    const network = new GroupNetwork(); networks.push(network); const owner = await network.client(61); const peer = await network.client(62); const relayId = network.network.descriptor.relayId;
    const group = (await owner.groups.createGroup(relayId, { name: 'Admission test', memberCapacity: 10 })).ref;
    const invite = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, invitee: peer.accountId });
    if (join) { await peer.groups.applyToGroup(invite); await owner.groups.approveApplications(group, [peer.accountId]); await peer.groups.getGroup(group); }
    return { network, owner, peer, relayId, group, invite };
}

test.each(['targeted', 'shareable'] as const)('a nonmember previews and applies with a %s invitation without reading member-only records', async kind => {
    const { network, owner, peer, group, invite: targeted } = await fixture();
    const invite = kind === 'targeted' ? targeted : await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, maxUses: 2 });
    const start = network.requests.length;
    const preview = await peer.groups.getGroup(invite); expect(preview.membership).toBe('unknown'); expect(preview.role).toBeUndefined();
    await peer.groups.applyToGroup(invite);
    const requests = network.requests.slice(start);
    expect(requests.filter(value => value.method === 'group.invite.resolve')).toEqual([]);
    expect(requests.filter(value => value.method === 'group.resolve').map(value => value.body)).toEqual([
        { group_id: group.groupId, invite_id: invite.document.inviteId }, { group_id: group.groupId, invite_id: invite.document.inviteId },
    ]);
    const applications = requests.filter(value => value.method === 'group.application.submit'); expect(applications).toHaveLength(1);
    const application = groupApplicationCodec.decode(applications[0]!.body);
    expect(application.account).toBe(peer.accountId); expect(application.inviteId).toBe(invite.document.inviteId);
    verifyGroupApplication(application, peer.device.certificate, context);
    expect((await all(peer.groups.getGroups({ membership: 'pending' })))[0]!.ref).toEqual(group);
    const local = await peer.store.read([{ collection: 'group_member_keys' }, { collection: 'group_operations' }, { collection: 'group_epochs' }]);
    expect(local.sets[0]).toHaveLength(1); expect(typeof local.sets[0]![0]!.value.protectedKey).toBe('string');
    expect(local.sets[1]).toEqual([]); expect(local.sets[2]).toEqual([]);
});

test.each(['preview', 'apply'] as const)('%s rejects invalid invitations and relay previews without creating local admission state', async operation => {
    const { network, owner, peer, group, invite } = await fixture();
    const key = `${group.groupId}|${invite.document.inviteId}`; const accepted = network.invites.get(key)!;
    const run = async (input: GroupInvitation) => operation === 'preview' ? peer.groups.getGroup(input) : peer.groups.applyToGroup(input);
    const start = network.requests.length;
    await expect(run({ ...invite, document: { ...invite.document, invitee: owner.accountId } })).rejects.toThrow('another account');
    await expect(run({ ...invite, document: { ...invite.document, expiresAt: network.network.clock.wall } })).rejects.toThrow('expire');
    expect(network.requests.slice(start)).toEqual([]);
    // Relay admission is based on its accepted record; the caller cannot replace its recipient.
    network.invites.set(key, { ...accepted, invite: { ...accepted.invite, invitee: owner.accountId } });
    await expect(run(invite)).rejects.toThrow('forbidden');
    network.invites.delete(key); await expect(run(invite)).rejects.toThrow('forbidden');
    network.invites.set(key, { ...accepted, uses: 1 }); await expect(run(invite)).rejects.toThrow('forbidden');
    network.invites.set(key, { ...accepted, invite: { ...accepted.invite, expiresAt: network.network.clock.wall } });
    await expect(run(invite)).rejects.toThrow('forbidden');
    network.invites.set(key, accepted);
    await owner.groups.ban(group, [peer.accountId]); await expect(run(invite)).rejects.toThrow('forbidden');
    await owner.groups.unban(group, [peer.accountId]);
    network.preview = state => ({ ...state, groupId: `grp_${'A'.repeat(22)}` }); await expect(run(invite)).rejects.toThrow('another group');
    expect(network.requests.slice(start).some(value => value.method === 'group.invite.resolve' || value.method === 'group.application.submit')).toBe(false);
    const local = await peer.store.read([{ collection: 'groups' }, { collection: 'group_member_keys' }, { collection: 'group_operations' }]);
    expect(local.sets).toEqual([[], [], []]);
});

test('previews never establish authority; application, protected member key and verified admission lead to encrypted messaging', async () => {
    const { network, owner, peer, group, invite } = await fixture();
    const preview = await peer.groups.getGroup(invite); expect(preview.membership).toBe('unknown'); expect(preview.role).toBeUndefined();
    expect((await peer.store.read([{ collection: 'groups' }])).sets[0]![0]!.value.projection).toBeUndefined(); expect((await peer.store.read([{ collection: 'group_epochs' }])).sets[0]).toEqual([]);
    await peer.groups.applyToGroup(invite); expect((await all(peer.groups.getGroups({ membership: 'pending' })))[0]!.ref).toEqual(group);
    const applications = await owner.groups.getApplications(group); expect(applications.items[0]!.application.account).toBe(peer.accountId);
    const keys = (await peer.store.read([{ collection: 'group_member_keys' }])).sets[0]!; expect(keys).toHaveLength(1); expect(keys[0]!.value.publicKey).toBe(encodeBase64Url(applications.items[0]!.application.memberEncryptionPublicKey)); expect(typeof keys[0]!.value.protectedKey).toBe('string');
    await owner.groups.approveApplications(group, [peer.accountId]); expect((await owner.groups.getApplications(group)).items).toEqual([]);
    const admitted = await peer.groups.getGroup(group); expect(admitted.membership).toBe('member'); expect(admitted.role).toBe('member');
    await peer.groups.setNickname(group, '新成员 😀'); const message = await peer.groups.sendMessage(group, { body: { contentType: 'text/plain', text: '批准后使用分发的群密钥' } });
    await owner.groups.getGroup(group); expect((await all(owner.groups.getMessages()))[0]!.messageId).toBe(message.messageId);
    expect((await all(owner.groups.getMembers(group))).find(value => value.accountId === peer.accountId)!.nickname).toBe('新成员 😀');
    expect(network.requests.filter(value => value.method === 'group.application.approve')).toHaveLength(1);
});

test('relay preview presentation fields cannot overwrite verified group state or grant the previewed owner authority', async () => {
    const { network, owner, peer, group, invite } = await fixture();
    const shareable = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600 });
    network.preview = value => ({ ...value, name: 'Untrusted preview', owner: peer.accountId });
    expect((await peer.groups.getGroup(invite)).group.owner).toBe(peer.accountId);
    const verified = await owner.groups.getGroup(shareable); expect(verified.group.owner).toBe(owner.accountId); expect(verified.group.name).toBe('Admission test');
    await expect(peer.groups.createInvite(group, { expiresAt: network.network.clock.wall + 60 })).rejects.toThrow('not writable');
    expect(network.requests.filter(value => value.method === 'group.invite.create')).toHaveLength(2);
});

test('batch admission traverses pages and seals distinct boxes to every signed member key', async () => {
    const { network, owner, peer, group } = await fixture(); const second = await network.client(63); const shareable = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, maxUses: 2 });
    await peer.groups.applyToGroup(shareable); await second.groups.applyToGroup(shareable); network.listPageSize = 1;
    await owner.groups.approveApplications(group, [second.accountId, peer.accountId]);
    await peer.groups.getGroup(group); await second.groups.getGroup(group);
    const sent = await peer.groups.sendMessage(group, { body: { contentType: 'text/plain', text: '两个成员独立恢复同一 epoch' } }); await second.groups.getGroup(group);
    expect((await all(second.groups.getMessages()))[0]!.messageId).toBe(sent.messageId);
    expect((await owner.groups.getInvite({ group, inviteId: shareable.document.inviteId })).uses).toBe(2);
    const request = network.requests.find(value => value.method === 'group.application.approve')!.body; expect(new Set(Object.keys(request.client_secret_boxes!))).toEqual(new Set([second.accountId, peer.accountId]));
});

test.each(['group.application.submit', 'group.application.approve'])('lost %s acknowledgement recovers the exact request and protected member key after restart', async method => {
    const { network, owner, peer, group, invite } = await fixture(); if (method === 'group.application.approve') await peer.groups.applyToGroup(invite);
    network.loseResponse = method; const actor = method === 'group.application.submit' ? peer : owner;
    await expect(method === 'group.application.submit' ? peer.groups.applyToGroup(invite) : owner.groups.approveApplications(group, [peer.accountId])).rejects.toThrow('response lost');
    const expected = (await actor.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value.request;
    await actor.dispose(); network.loseResponse = undefined; const resumed = await network.client(method === 'group.application.submit' ? 62 : 61, actor.path);
    await resumed.groups.start(); await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    const submissions = network.requests.filter(value => value.method === method); expect(submissions).toHaveLength(method === 'group.application.submit' ? 2 : 1);
    expect(submissions.every(value => canonicalJson(value.body) === canonicalJson(expected!))).toBe(true);
    if (method === 'group.application.submit') { await owner.groups.approveApplications(group, [peer.accountId]); await resumed.groups.getGroup(group); await resumed.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'recovered admission key' } }); }
    else { expect((await peer.groups.getGroup(group)).membership).toBe('member'); }
});

test.each([false, true])('an application approved while its submitting device is offline reconciles without resubmission after later key replacement=%s', async replaced => {
    const { network, owner, peer, group, invite } = await fixture();
    network.loseResponse = 'group.application.submit';
    await expect(peer.groups.applyToGroup(invite)).rejects.toThrow('response lost');
    const savedKeys = (await peer.store.read([{ collection: 'group_member_keys' }])).sets[0]!;
    await peer.dispose(); network.loseResponse = undefined;
    await owner.groups.approveApplications(group, [peer.accountId]);
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'approved while applicant was offline' } });
    if (replaced) {
        const replacement = await network.client(62);
        await replacement.groups.requestKeyRecovery(group);
        await owner.groups.approveKeyRecovery(group, [peer.accountId]);
    }
    const resumed = await network.client(62, peer.path); const errors: unknown[] = [];
    resumed.groups.onLifecycle('backgroundError', value => { errors.push(value.error); });
    await resumed.groups.start();
    await network.network.until(async () => (await all(resumed.groups.getMessages())).some(value => value.body?.text === 'approved while applicant was offline'));
    await resumed.groups.stop();
    expect((await resumed.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual([]);
    expect((await resumed.store.read([{ collection: 'group_member_keys' }])).sets[0]!.map(row => row.value.protectedKey)).toEqual(savedKeys.map(row => row.value.protectedKey));
    expect(network.requests.filter(value => value.method === 'group.application.submit')).toHaveLength(1);
    expect(errors).toEqual([]);
    expect((await all(resumed.groups.getGroups()))[0]!.membership).toBe('member');
    if (!replaced) {
        await resumed.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'reconciled applicant can send' } });
        await owner.groups.getGroup(group);
        expect((await all(owner.groups.getMessages())).at(-1)!.body!.text).toBe('reconciled applicant can send');
    }
});

test('approval of another device candidate cannot confirm the original uncertain application', async () => {
    const { network, owner, peer, group, invite } = await fixture();
    network.loseResponse = 'group.application.submit';
    await expect(peer.groups.applyToGroup(invite)).rejects.toThrow('response lost');
    const original = (await peer.store.read([{ collection: 'group_operations' }])).sets[0]!;
    await peer.dispose(); network.loseResponse = undefined;
    const replacement = await network.client(62); await replacement.groups.applyToGroup(invite);
    await owner.groups.approveApplications(group, [peer.accountId]);
    const resumed = await network.client(62, peer.path); const errors: unknown[] = [];
    resumed.groups.onLifecycle('backgroundError', value => { errors.push(value.error); });
    await resumed.groups.start();
    await network.network.until(async () => errors.length > 0 && (await all(resumed.groups.getMembers(group))).some(member => member.accountId === peer.accountId));
    await resumed.groups.stop();
    expect((await resumed.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual(original);
    expect(network.requests.filter(value => value.method === 'group.application.submit' && canonicalJson(value.body) === canonicalJson(original[0]!.value.request!)).length).toBeGreaterThan(1);
});

test('a changed application signature cannot cause a secret box to be published', async () => {
    const { network, owner, peer, group, invite } = await fixture(); await peer.groups.applyToGroup(invite);
    network.applicationPage = page => ({ ...page, applications: page.applications.map(value => ({ ...value, signerCertificate: owner.device.certificate })) });
    await expect(owner.groups.approveApplications(group, [peer.accountId])).rejects.toThrow('another account');
    expect(network.requests.filter(value => value.method === 'group.application.approve')).toEqual([]); expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual([]);
});

test('a valid signed application tied to another targeted invite is rejected before client-secret wrapping', async () => {
    const { network, owner, peer, group, invite } = await fixture(); await peer.groups.applyToGroup(invite);
    const other = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, invitee: owner.accountId });
    const original = network.applications.get(`${group.groupId}|${peer.accountId}`)!;
    let application = { ...original.application, inviteId: other.document.inviteId }; application = { ...application, deviceSignature: await peer.device.sign(groupApplicationInput(application, context)) };
    network.applications.set(`${group.groupId}|${peer.accountId}`, { ...original, application });
    await expect(owner.groups.approveApplications(group, [peer.accountId])).rejects.toThrow('targets another account'); expect(network.requests.filter(value => value.method === 'group.application.approve')).toEqual([]);
});

test('selected applications cannot overrun remaining shared invitation uses', async () => {
    const { network, owner, peer, group } = await fixture(); const second = await network.client(63); const shareable = await owner.groups.createInvite(group, { expiresAt: network.network.clock.wall + 3600, maxUses: 1 });
    await peer.groups.applyToGroup(shareable); await second.groups.applyToGroup(shareable);
    await expect(owner.groups.approveApplications(group, [peer.accountId, second.accountId])).rejects.toThrow('remaining invitation'); expect(network.requests.filter(value => value.method === 'group.application.approve')).toEqual([]);
});

test('request pagination rejects duplicated accounts and empty continuation pages', async () => {
    const { network, owner, peer, group, invite } = await fixture(); await peer.groups.applyToGroup(invite); const original = network.applications.get(`${group.groupId}|${peer.accountId}`)!;
    network.applicationPage = page => ({ applications: [original], next: page.next === undefined ? 'again' : page.next });
    await expect(owner.groups.approveApplications(group, [peer.accountId, owner.accountId])).rejects.toThrow();
    network.applicationPage = () => ({ applications: [], next: 'empty' }); await expect(owner.groups.getApplications(group)).rejects.toThrow('empty');
    expect(network.requests.filter(value => value.method === 'group.application.approve')).toEqual([]);
});

test('fresh device missing member keys recovers only after approval and can decrypt a later epoch without older keys', async () => {
    const { network, owner, peer, group } = await fixture(true); await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'old inaccessible message' } });
    const recovered = await network.client(62); await recovered.groups.getGroup(group); expect(await all(recovered.groups.getMessages())).toEqual([]);
    const before = (await all(recovered.groups.getMembers(group))).find(value => value.accountId === peer.accountId)!.memberEncryptionPublicKey;
    const pending = await recovered.groups.requestKeyRecovery(group); expect(pending.request.account).toBe(peer.accountId); expect(pending.expiresAt).toBeGreaterThan(pending.acceptedAt);
    expect(pending.request.memberEncryptionPublicKey).not.toEqual(before); expect((await all(recovered.groups.getMembers(group))).find(value => value.accountId === peer.accountId)!.memberEncryptionPublicKey).toEqual(before);
    expect((await owner.groups.getKeyRecoveryRequests(group)).items[0]!.request).toEqual(pending.request);
    await owner.groups.approveKeyRecovery(group, [peer.accountId]); await recovered.groups.getGroup(group);
    expect((await all(recovered.groups.getMembers(group))).find(value => value.accountId === peer.accountId)!.memberEncryptionPublicKey).toEqual(pending.request.memberEncryptionPublicKey);
    const current = await recovered.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'new member key is active' } }); await owner.groups.getGroup(group);
    expect((await all(owner.groups.getMessages())).at(-1)!.messageId).toBe(current.messageId); expect((await all(recovered.groups.getMessages())).map(value => value.body!.text)).toEqual(['new member key is active']);
});

test('withdrawing and rejecting requests leaves membership, member keys and group timeline unchanged', async () => {
    const { network, owner, peer, group, invite } = await fixture(); await peer.groups.applyToGroup(invite); await owner.groups.rejectApplications(group, [peer.accountId]); expect((await owner.groups.getApplications(group)).items).toEqual([]);
    expect(network.groups.get(group.groupId)!.events).toHaveLength(1); await peer.groups.applyToGroup(invite); await owner.groups.approveApplications(group, [peer.accountId]); await peer.groups.getGroup(group);
    const before = await all(peer.groups.getMembers(group)); const sequence = network.groups.get(group.groupId)!.events.length;
    await peer.groups.requestKeyRecovery(group); await peer.groups.withdrawKeyRecovery(group); expect((await owner.groups.getKeyRecoveryRequests(group)).items).toEqual([]);
    await peer.groups.requestKeyRecovery(group); await owner.groups.rejectKeyRecovery(group, [peer.accountId]); expect((await owner.groups.getKeyRecoveryRequests(group)).items).toEqual([]);
    expect(await all(peer.groups.getMembers(group))).toEqual(before); expect(network.groups.get(group.groupId)!.events).toHaveLength(sequence);
});

test.each(['group.member.recovery.submit', 'group.member.recovery.approve'])('lost %s response retains the original candidate key during durable recovery', async method => {
    const { network, owner, peer, group } = await fixture(true); const candidate = method === 'group.member.recovery.approve' ? await peer.groups.requestKeyRecovery(group) : undefined;
    network.loseResponse = method; const actor = method.endsWith('.submit') ? peer : owner;
    await expect(method.endsWith('.submit') ? peer.groups.requestKeyRecovery(group) : owner.groups.approveKeyRecovery(group, [peer.accountId])).rejects.toThrow('response lost');
    const pending = (await actor.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value.request;
    network.loseResponse = undefined; await actor.dispose(); const resumed = await network.client(method.endsWith('.submit') ? 62 : 61, actor.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    const requests = network.requests.filter(value => value.method === method); expect(requests).toHaveLength(method.endsWith('.submit') ? 2 : 1); expect(requests.every(value => canonicalJson(value.body) === canonicalJson(pending!))).toBe(true);
    if (candidate) { await peer.groups.getGroup(group); expect((await all(peer.groups.getMembers(group))).find(value => value.accountId === peer.accountId)!.memberEncryptionPublicKey).toEqual(candidate.request.memberEncryptionPublicKey); }
});

test.each(['lost', 'malformed', 'replaced'] as const)('an offline-approved recovery submission reconciles its original candidate after a %s response/state', async scenario => {
    const { network, owner, peer, group } = await fixture(true);
    const handle = network.network.handleRequest!;
    if (scenario === 'malformed') {
        network.network.handleRequest = async (method, ...args) => {
            const response = await handle(method, ...args);
            return method === 'group.member.recovery.submit'
                ? new Response(JSON.stringify({ accepted_at: 0, expires_at: 0 }), { status: 200, headers: { 'content-type': 'application/json' } }) : response;
        };
        await expect(peer.groups.requestKeyRecovery(group)).rejects.toMatchObject({ code: 'invalid_expiry' });
    } else {
        network.loseResponse = 'group.member.recovery.submit';
        await expect(peer.groups.requestKeyRecovery(group)).rejects.toThrow('response lost');
    }
    const savedKeys = (await peer.store.read([{ collection: 'group_member_keys' }])).sets[0]!;
    const candidate = (await owner.groups.getKeyRecoveryRequests(group)).items[0]!;
    await peer.dispose(); network.loseResponse = undefined; network.network.handleRequest = handle;
    await owner.groups.approveKeyRecovery(group, [peer.accountId]);
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'recovery approved while device was offline' } });
    if (scenario === 'replaced') {
        const replacement = await network.client(62); await replacement.groups.requestKeyRecovery(group);
        await owner.groups.approveKeyRecovery(group, [peer.accountId]);
    }
    const resumed = await network.client(62, peer.path); const errors: unknown[] = [];
    resumed.groups.onLifecycle('backgroundError', value => { errors.push(value.error); });
    await resumed.groups.start();
    await network.network.until(async () => (await all(resumed.groups.getMessages())).some(message => message.body?.text === 'recovery approved while device was offline'));
    await resumed.groups.stop();
    expect((await resumed.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual([]);
    expect((await resumed.store.read([{ collection: 'group_member_keys' }])).sets[0]!.map(row => row.value.protectedKey)).toEqual(savedKeys.map(row => row.value.protectedKey));
    expect(network.requests.filter(value => value.method === 'group.member.recovery.submit' && canonicalJson(value.body) === canonicalJson(groupMemberRecoveryRequestCodec.encode(candidate.request)))).toHaveLength(1);
    expect((await owner.groups.getKeyRecoveryRequests(group)).items).toEqual([]);
    expect(errors).toEqual([]);
    if (scenario !== 'replaced') {
        await resumed.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'original recovery candidate can send' } });
        await owner.groups.getGroup(group);
        expect((await all(owner.groups.getMessages())).at(-1)!.body!.text).toBe('original recovery candidate can send');
    }
});

test('approval of another recovery candidate cannot confirm or activate the original request', async () => {
    const { network, owner, peer, group } = await fixture(true);
    network.loseResponse = 'group.member.recovery.submit';
    await expect(peer.groups.requestKeyRecovery(group)).rejects.toThrow('response lost');
    const original = (await owner.groups.getKeyRecoveryRequests(group)).items[0]!.request;
    await peer.dispose(); network.loseResponse = undefined;
    const other = await network.client(62); const replacement = await other.groups.requestKeyRecovery(group);
    await owner.groups.approveKeyRecovery(group, [peer.accountId]);
    const resumed = await network.client(62, peer.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length);
    await resumed.groups.stop();
    expect((await owner.groups.getKeyRecoveryRequests(group)).items[0]!.request).toEqual(original);
    expect(network.requests.filter(value => value.method === 'group.member.recovery.submit' && canonicalJson(value.body) === canonicalJson(groupMemberRecoveryRequestCodec.encode(original)))).toHaveLength(2);
    expect((await all(resumed.groups.getMembers(group))).find(member => member.accountId === peer.accountId)!.memberEncryptionPublicKey).toEqual(replacement.request.memberEncryptionPublicKey);
    await expect(resumed.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'unapproved candidate' } })).rejects.toMatchObject({ name: 'GroupKeyAccessError' });
});

test('expired recovery requests cannot be approved and historical signer identity cannot be substituted', async () => {
    const { network, owner, peer, group } = await fixture(true); await peer.groups.requestKeyRecovery(group); network.network.clock.tick();
    network.recoveryPage = page => ({ ...page, requests: page.requests.map(value => ({ ...value, expiresAt: network.network.clock.wall })) });
    await expect(owner.groups.approveKeyRecovery(group, [peer.accountId])).rejects.toThrow('expired');
    network.recoveryPage = page => ({ ...page, requests: page.requests.map(value => ({ ...value, signerCertificate: owner.device.certificate })) });
    await expect(owner.groups.approveKeyRecovery(group, [peer.accountId])).rejects.toThrow('another account');
    expect(network.requests.filter(value => value.method === 'group.member.recovery.approve')).toEqual([]);
});

test.each([true, false])('recovery submission acceptance interval valid=%s controls completion without losing an accepted request', async valid => {
    const { network, peer, group } = await fixture(true);
    const handle = network.network.handleRequest!;
    network.network.handleRequest = async (method, ...args) => {
        const result = await handle(method, ...args);
        return method === 'group.member.recovery.submit'
            ? new Response(JSON.stringify({ accepted_at: 0, expires_at: valid ? 1 : 0 }), { status: 200, headers: { 'content-type': 'application/json' } })
            : result;
    };
    if (valid) {
        const result = await peer.groups.requestKeyRecovery(group);
        expect({ acceptedAt: result.acceptedAt, expiresAt: result.expiresAt }).toEqual({ acceptedAt: 0, expiresAt: 1 });
        expect((await peer.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual([]);
    } else {
        await expect(peer.groups.requestKeyRecovery(group)).rejects.toMatchObject({ code: 'invalid_expiry' });
        const queries = [{ collection: 'group_operations' }, { collection: 'group_member_keys' }];
        const before = (await peer.store.read(queries)).sets;
        expect(before[0]).toHaveLength(1);
        expect(before[0]![0]!.value).toMatchObject({ method: 'group.member.recovery.submit', accepted: true });
        expect(before[0]![0]!.value.acceptedResult).toBeUndefined();
        expect(before[1]).toHaveLength(2);
        await peer.dispose(); const resumed = await network.client(62, peer.path); const errors: unknown[] = [];
        resumed.groups.onLifecycle('backgroundError', value => { errors.push(value.error); });
        await resumed.groups.start();
        await network.network.until(async () => errors.some(error => error instanceof ProtocolError && error.code === 'unconfirmed_recovery'));
        await resumed.groups.stop();
        expect((await resumed.store.read(queries)).sets).toEqual(before);
    }
    expect(network.requests.filter(value => value.method === 'group.member.recovery.submit')).toHaveLength(1);
});
