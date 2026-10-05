import { afterEach, expect, test } from 'vitest';
import { GroupManager, canonicalJson, groupMessageEnvelopeCodec, groupSyncPageCodec, type QueryReader, type GroupEvents } from '@meshline/sdk';
import { GroupNetwork } from '../support/group-network.js';
const networks: GroupNetwork[] = []; afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
async function fixture() { const network = new GroupNetwork(); networks.push(network); const owner = await network.client(51); const peer = await network.client(52); return { network, owner, peer, relayId: network.network.descriptor.relayId }; }
async function all<T>(query: Promise<QueryReader<T>>): Promise<readonly T[]> { const reader = await query; try { return await reader.readNext(100); } finally { await reader.dispose(); } }

test.each([false, true])('message batches follow group then sequence despite older creation times (filter group: %s)', async filterGroup => {
    const { network, owner, relayId } = await fixture();
    const groups = [(await owner.groups.createGroup(relayId, { name: 'one', memberCapacity: 2 })).ref,
        (await owner.groups.createGroup(relayId, { name: 'two', memberCapacity: 2 })).ref].sort((a, b) => a.groupId < b.groupId ? -1 : 1);
    const first = groups[0]!; const second = groups[1]!; network.network.clock.wall += 60;
    await owner.groups.sendMessage(second, { body: { contentType: 'text/plain', text: 'second group first' } });
    await owner.groups.sendMessage(first, { body: { contentType: 'text/plain', text: 'first group first' } });
    network.network.clock.wall -= 20;
    await owner.groups.sendMessage(second, { body: { contentType: 'text/plain', text: 'second group second' } });
    await owner.groups.sendMessage(first, { body: { contentType: 'text/plain', text: 'first group second' } });
    const reader = await owner.groups.getMessages(filterGroup ? { groupId: first.groupId } : {});
    try {
        for (const text of filterGroup ? ['first group first', 'first group second'] : ['first group first', 'first group second', 'second group first', 'second group second'])
            expect((await reader.readNext(1))[0]!.body!.text).toBe(text);
        expect(await reader.readNext(1)).toEqual([]);
    } finally { await reader.dispose(); }
});

test('an explicitly empty hosting-relay filter cannot expand a group query to all relays', async () => {
    const { owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'scoped query', memberCapacity: 2 });
    expect((await all(owner.groups.getGroups({ relayId }))).map(value => value.ref)).toEqual([group.ref]);
    await expect(all(Promise.resolve().then(() => owner.groups.getGroups({ relayId: '' })))).rejects.toMatchObject({ code: 'invalid_relay' });
    expect((await all(owner.groups.getGroups())).map(value => value.ref)).toEqual([group.ref]);
});

test('GroupManager creates, updates, encrypts, queries, clears nicknames, bans and closes with verified state', async () => {
    const { network, owner, peer, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: '加密群', description: '简介', memberCapacity: 10 });
    expect(owner.groups).toBeInstanceOf(GroupManager); expect(group.membership).toBe('member'); expect(group.role).toBe('owner');
    const updated = await owner.groups.updateGroup(group.ref, { name: '更名', description: null }); expect(updated.group.name).toBe('更名'); expect(updated.group.description).toBeUndefined();
    await owner.groups.setNickname(group.ref, '群内昵称 😀'); expect((await all(owner.groups.getMembers(group.ref)))[0]!.nickname).toBe('群内昵称 😀');
    const reader = await owner.groups.getMembers(group.ref); await owner.groups.setNickname(group.ref, null); expect((await reader.readNext(10))[0]!.nickname).toBe('群内昵称 😀'); await reader.dispose();
    expect((await all(owner.groups.getMembers(group.ref)))[0]!.nickname).toBeUndefined();
    const message = await owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: '端到端群消息' } });
    const reply = await owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: '回复' }, replyToSeq: message.sequence }); expect(reply.replyToSeq).toBe(message.sequence);
    expect(await all(owner.groups.getMessages({ groupId: group.ref.groupId, sender: owner.accountId }))).toHaveLength(2);
    expect(canonicalJson(network.requests.filter(value => value.method === 'group.message.send').map(value => value.body))).not.toContain('端到端群消息');
    await owner.groups.ban(group.ref, [peer.accountId]); expect(await all(owner.groups.getBans(group.ref))).toEqual([peer.accountId]); await owner.groups.unban(group.ref, [peer.accountId]); expect(await all(owner.groups.getBans(group.ref))).toEqual([]);
    expect(await all(owner.groups.getGroups({ role: 'owner', membership: 'member' }))).toHaveLength(1);
    await owner.groups.closeGroup(group.ref); expect((await all(owner.groups.getGroups()))[0]!.group.status).toBe('closed'); await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'closed' } })).rejects.toThrow('not writable');
});

test('group change events classify management and nicknames without reporting ordinary messages as group changes', async () => {
    const { owner, peer, relayId } = await fixture(); const changes: GroupEvents['groupChanged'][] = [];
    owner.groups.on('groupChanged', value => { changes.push(value); });
    const { ref } = await owner.groups.createGroup(relayId, { name: 'original', memberCapacity: 10 });
    await owner.groups.updateGroup(ref, { name: 'updated' }); await owner.groups.setNickname(ref, 'nickname');
    await owner.groups.sendMessage(ref, { body: { contentType: 'text/plain', text: 'ordinary message' } });
    await owner.groups.ban(ref, [peer.accountId]); await owner.groups.unban(ref, [peer.accountId]); await owner.groups.closeGroup(ref);
    expect(changes.map(value => value.kinds)).toEqual([['properties', 'members', 'roles', 'status'], ['properties'], ['nickname'], ['members', 'bans'], ['bans'], ['status']]);
    expect(changes[0]).toMatchObject({ ref, group: { name: 'original', status: 'active' }, membership: 'member', role: 'owner' });
    expect(changes[1]).toMatchObject({ group: { name: 'updated', status: 'active' } }); expect(changes.at(-1)).toMatchObject({ group: { status: 'closed' } });
});

test('local application and departure events preserve membership snapshots alongside role changes', async () => {
    const { network, owner, peer, relayId } = await fixture(); const { ref } = await owner.groups.createGroup(relayId, { name: 'membership events', memberCapacity: 10 });
    const invite = await owner.groups.createInvite(ref, { invitee: peer.accountId, expiresAt: network.network.clock.wall + 3600 });
    const peerChanges: GroupEvents['groupChanged'][] = []; const ownerChanges: GroupEvents['groupChanged'][] = [];
    peer.groups.on('groupChanged', value => { peerChanges.push(value); }); owner.groups.on('groupChanged', value => { ownerChanges.push(value); });
    await peer.groups.applyToGroup(invite); expect(peerChanges).toMatchObject([{ kinds: ['members'], membership: 'pending' }]);
    await owner.groups.approveApplications(ref, [peer.accountId]); await peer.groups.getGroup(ref); await owner.groups.setRole(ref, peer.accountId, 'administrator'); await peer.groups.getGroup(ref);
    expect(ownerChanges.map(value => value.kinds)).toEqual([['members'], ['roles']]); expect(peerChanges.at(-1)).toMatchObject({ kinds: ['roles'], role: 'administrator' });
    await peer.groups.leaveGroup(ref); expect(peerChanges.at(-1)).toMatchObject({ kinds: ['members'], membership: 'left' }); expect(peerChanges.at(-1)!.role).toBeUndefined();
    expect(peerChanges[0]!.membership).toBe('pending');
});

test('each committed history page publishes its snapshot even if the following page fails', async () => {
    const { network, owner, peer, relayId } = await fixture(); const { ref } = await owner.groups.createGroup(relayId, { name: 'before pages', memberCapacity: 10 });
    const invite = await owner.groups.createInvite(ref, { invitee: peer.accountId, expiresAt: network.network.clock.wall + 3600 });
    await peer.groups.applyToGroup(invite); await owner.groups.approveApplications(ref, [peer.accountId]); await peer.groups.getGroup(ref);
    await owner.groups.updateGroup(ref, { name: 'first page' }); await owner.groups.updateGroup(ref, { name: 'second page' });
    const changes: GroupEvents['groupChanged'][] = []; peer.groups.on('groupChanged', value => { changes.push(value); });
    const handle = network.network.handleRequest!; let pages = 0;
    network.network.handleRequest = async (method, ...args) => {
        if (method === 'group.sync' && ++pages === 2) throw new Error('next page unavailable');
        const response = await handle(method, ...args);
        if (method !== 'group.sync' || !response?.ok) return response;
        const page = groupSyncPageCodec.decode(await response.json()); return new Response(JSON.stringify(groupSyncPageCodec.encode({ ...page, events: page.events.slice(0, 1), hasMore: true })), { headers: { 'content-type': 'application/json' } });
    };
    try { await expect(peer.groups.getGroup(ref)).rejects.toThrow('next page unavailable'); }
    finally { network.network.handleRequest = handle; }
    expect(changes).toMatchObject([{ kinds: ['properties'], group: { name: 'first page' } }]);
    await peer.groups.getGroup(ref); expect(changes.map(value => value.group.name)).toEqual(['first page', 'second page']);
});

test('member queries use binary identifier order, including uppercase versus lowercase address characters', async () => {
    const network = new GroupNetwork(); networks.push(network); const owner = await network.client(41); const peer = await network.client(42);
    const group = (await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'Member ordering', memberCapacity: 10 })).ref;
    const invite = await owner.groups.createInvite(group, { invitee: peer.accountId, expiresAt: network.network.clock.wall + 3600 });
    await peer.groups.applyToGroup(invite); await owner.groups.approveApplications(group, [peer.accountId]);
    expect(owner.accountId).toContain(':NM'); expect(peer.accountId).toContain(':Ng');
    expect((await all(owner.groups.getMembers(group))).map(member => member.accountId)).toEqual([owner.accountId, peer.accountId]);
});

test('permissions, empty updates, invalid replies and relay capacity are rejected before publication', async () => {
    const { network, owner, peer, relayId } = await fixture(); await expect(owner.groups.createGroup(relayId, { name: 'too large', memberCapacity: 101 })).rejects.toThrow('capacity');
    const group = await owner.groups.createGroup(relayId, { name: 'permissions', memberCapacity: 10 });
    await expect(owner.groups.updateGroup(group.ref, {})).rejects.toThrow('modifiable'); await expect(owner.groups.leaveGroup(group.ref)).rejects.toThrow();
    await expect(owner.groups.transferOwnership(group.ref, peer.accountId)).rejects.toThrow(); await expect(peer.groups.updateGroup(group.ref, { name: 'outsider' })).rejects.toThrow('not writable');
    await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'invalid future reply' }, replyToSeq: 100 })).rejects.toThrow('preceding');
    expect(network.requests.filter(value => ['group.update', 'group.member.leave', 'group.owner.transfer', 'group.message.send'].includes(value.method))).toEqual([]);
});

test.each(['group.create', 'group.update', 'group.message.send'])('lost %s response reconciles verified history after restart without resubmission', async method => {
    const { network, owner, relayId } = await fixture(); let ref;
    if (method !== 'group.create') ref = (await owner.groups.createGroup(relayId, { name: 'recover', memberCapacity: 10 })).ref;
    network.loseResponse = method;
    const action = method === 'group.create' ? owner.groups.createGroup(relayId, { name: 'recover create', memberCapacity: 10 }) : method === 'group.update' ? owner.groups.updateGroup(ref!, { name: 'accepted update' }) : owner.groups.sendMessage(ref!, { body: { contentType: 'text/plain', text: 'accepted encrypted message' } });
    await expect(action).rejects.toThrow('response lost'); const pending = (await owner.store.read([{ collection: 'group_operations' }])).sets[0]!; expect(pending).toHaveLength(1);
    await owner.dispose(); network.loseResponse = undefined; const resumed = await network.client(51, owner.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    expect(network.requests.filter(value => value.method === method)).toHaveLength(1);
    expect((await all(resumed.groups.getGroups()))[0]!.group.name).toBe(method === 'group.create' ? 'recover create' : method === 'group.update' ? 'accepted update' : 'recover');
    if (method === 'group.message.send') expect((await all(resumed.groups.getMessages()))[0]!.body!.text).toBe('accepted encrypted message');
});

test('an unaccepted uncertain request retries exactly the same encrypted envelope after restart', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'exact retry', memberCapacity: 10 }); network.failBeforeAccept = 'group.message.send';
    await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'stable ciphertext' } })).rejects.toThrow('Connection lost');
    const pending = (await owner.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value; const expected = groupMessageEnvelopeCodec.decode(pending.request!);
    await owner.dispose(); network.failBeforeAccept = undefined; const resumed = await network.client(51, owner.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    const sent = network.requests.filter(value => value.method === 'group.message.send'); expect(sent).toHaveLength(2); expect(sent[0]!.body).toEqual(sent[1]!.body); expect((await all(resumed.groups.getMessages()))[0]!.messageId).toBe(expected.messageId);
});

test('positive acknowledgement survives failed follow-up history and never sends the message twice', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'retain ACK', memberCapacity: 10 }); network.failReadAfterSend = true;
    await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'acknowledged' } })).rejects.toThrow('history unavailable');
    const row = (await owner.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value; expect(row.accepted).toBe(true); expect(row.acceptedSequence).toBe(1);
    await owner.dispose(); network.failRead = false; network.failReadAfterSend = false; const resumed = await network.client(51, owner.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop(); expect(network.requests.filter(value => value.method === 'group.message.send')).toHaveLength(1);
});

test('an incorrect acknowledged sequence remains pending and visible without automatic resubmission', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'wrong ACK', memberCapacity: 10 }); network.wrongSequence = true;
    await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'must not duplicate' } })).rejects.toThrow('sequence differ');
    await owner.groups.start(); await network.network.until(async () => !!owner.groups.lastBackgroundError); await owner.groups.stop();
    expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value.acceptedSequence).toBe(101); expect(network.requests.filter(value => value.method === 'group.message.send')).toHaveLength(1);
});

test('successful HTTP acceptance with a malformed receipt is persisted before response validation fails', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'malformed ACK', memberCapacity: 10 }); network.missingSequence = true; network.failReadAfterSend = true;
    await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'accepted despite malformed receipt' } })).rejects.toThrow();
    expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]![0]!.value.accepted).toBe(true);
    await owner.dispose(); network.failRead = false; const resumed = await network.client(51, owner.path); await resumed.groups.start();
    await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    expect(network.requests.filter(value => value.method === 'group.message.send')).toHaveLength(1); expect((await all(resumed.groups.getMessages()))[0]!.body!.text).toBe('accepted despite malformed receipt');
});

test('a definitive first rejection clears the operation while rejection after uncertainty preserves it', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'reject', memberCapacity: 10 }); network.rejectMethod = 'group.update';
    await expect(owner.groups.updateGroup(group.ref, { name: 'rejected' })).rejects.toThrow('state_conflict'); expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]).toEqual([]);
    network.rejectMethod = undefined; network.failBeforeAccept = 'group.update'; await expect(owner.groups.updateGroup(group.ref, { name: 'uncertain' })).rejects.toThrow('Connection lost');
    network.failBeforeAccept = undefined; network.rejectMethod = 'group.update'; await owner.groups.start(); await network.network.until(async () => !!owner.groups.lastBackgroundError); await owner.groups.stop();
    expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]).toHaveLength(1); await expect(owner.groups.updateGroup(group.ref, { name: 'replacement' })).rejects.toThrow('awaiting confirmation');
});

test('background timeline observers can stop the manager without deadlocking committed recovery', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'observer', memberCapacity: 10 }); network.loseResponse = 'group.message.send';
    await expect(owner.groups.sendMessage(group.ref, { body: { contentType: 'text/plain', text: 'observer recovery' } })).rejects.toThrow('response lost'); network.loseResponse = undefined;
    let stopped = false; owner.groups.on('timelineChanged', async () => { await owner.groups.stop(); stopped = true; }); await owner.groups.start(); await network.network.until(async () => stopped);
    expect((await all(owner.groups.getMessages()))[0]!.body!.text).toBe('observer recovery'); expect(owner.groups.lifecycleState).toBe('stopped');
});

test('signed targeted/shareable invitations resolve, paginate and revoke without changing the group timeline', async () => {
    const { network, owner, peer, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'invitations', memberCapacity: 10 });
    const expiresAt = network.network.clock.wall + 1200;
    const targeted = await owner.groups.createInvite(group.ref, { expiresAt, invitee: peer.accountId });
    const shareable = await owner.groups.createInvite(group.ref, { expiresAt, maxUses: 3 });
    await expect(peer.groups.getInvite({ group: group.ref, inviteId: targeted.document.inviteId })).rejects.toThrow('forbidden');
    expect((await owner.groups.getInvite({ group: group.ref, inviteId: targeted.document.inviteId })).invite.document.invitee).toBe(peer.accountId);
    const first = await owner.groups.getInvites(group.ref, { limit: 1 }); expect(first.items).toHaveLength(1); expect(first.nextCursor).toBeDefined();
    const second = await owner.groups.getInvites(group.ref, { limit: 1, cursor: first.nextCursor! }); expect(second.items).toHaveLength(1); expect(second.nextCursor).toBeUndefined();
    expect(new Set([...first.items, ...second.items].map(value => value.invite.document.inviteId))).toEqual(new Set([targeted.document.inviteId, shareable.document.inviteId]));
    await owner.groups.revokeInvite({ group: group.ref, inviteId: targeted.document.inviteId }); expect((await owner.groups.getInvites(group.ref)).items).toHaveLength(1);
    await expect(owner.groups.getInvite({ group: group.ref, inviteId: targeted.document.inviteId })).rejects.toThrow('not_found');
    expect(network.groups.get(group.ref.groupId)!.events).toHaveLength(1);
});

test('invitation lifetime, field combinations and issuing permissions are checked before submission', async () => {
    const { network, owner, peer, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'invite checks', memberCapacity: 10 }); const now = network.network.clock.wall;
    await expect(owner.groups.createInvite(group.ref, { expiresAt: now + 86401 })).rejects.toThrow('lifetime');
    await expect(owner.groups.createInvite(group.ref, { expiresAt: now + 60, invitee: peer.accountId, maxUses: 1 })).rejects.toThrow('shareable');
    await expect(owner.groups.createInvite(group.ref, { expiresAt: now })).rejects.toThrow('expire');
    await expect(peer.groups.createInvite(group.ref, { expiresAt: now + 60 })).rejects.toThrow('not writable');
    expect(network.requests.filter(value => value.method === 'group.invite.create')).toEqual([]);
});

test('lost invitation creation is confirmed against the exact stored signed invitation after restart', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'invite recovery', memberCapacity: 10 }); network.loseResponse = 'group.invite.create';
    await expect(owner.groups.createInvite(group.ref, { expiresAt: network.network.clock.wall + 3600 })).rejects.toThrow('response lost');
    expect((await owner.store.read([{ collection: 'group_operations' }])).sets[0]).toHaveLength(1); await owner.dispose(); network.loseResponse = undefined;
    const resumed = await network.client(51, owner.path); await resumed.groups.start(); await network.network.until(async () => !(await resumed.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await resumed.groups.stop();
    expect(network.requests.filter(value => value.method === 'group.invite.create')).toHaveLength(1); expect((await resumed.groups.getInvites(group.ref)).items).toHaveLength(1);
});

test('unaccepted uncertain invitations retry the original signature and identifier', async () => {
    const { network, owner, relayId } = await fixture(); const group = await owner.groups.createGroup(relayId, { name: 'invite retry', memberCapacity: 10 }); network.failBeforeAccept = 'group.invite.create';
    await expect(owner.groups.createInvite(group.ref, { expiresAt: network.network.clock.wall + 3600, maxUses: 2 })).rejects.toThrow('Connection lost'); network.failBeforeAccept = undefined;
    await owner.groups.start(); await network.network.until(async () => !(await owner.store.read([{ collection: 'group_operations' }])).sets[0]!.length); await owner.groups.stop();
    const requests = network.requests.filter(value => value.method === 'group.invite.create'); expect(requests).toHaveLength(2); expect(requests[0]!.body).toEqual(requests[1]!.body);
});
