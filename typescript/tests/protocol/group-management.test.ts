import { expect, test } from 'vitest';
import {
    certificateId, decodeBase64Url, encodeBase64Url, groupApplicationCodec, groupApplicationInput, groupEventCodec, groupManagementInput, groupManagementPayloadCodec,
    groupMemberRecoveryRequestCodec, groupMemberRecoveryRequestInput, groupSyncPageCodec, validateGroupSyncPage, verifyGroupApplication, verifyGroupMemberRecoveryRequest,
    deviceCertificateCodec, signDevice, signingInput, groupInviteCodec, groupInviteInput, verifyGroupInvite, validateGroupInvite, groupStateCodec, validateGroupState,
    type JsonObject, type GroupEvent,
} from '@meshline/sdk';
import { applyGroupEvent, type GroupProjection } from '../../packages/sdk/dist/groups/state.js';
import { groupVectors, groupContext as context, groupReference as group, groupActors as actors, groupSnapshot, applyFixtureEvent, groupProjectionWire, signedGroupEvent } from '../support/group-fixture.js';
import { hex } from '../support/vectors.js';
const data = groupVectors.management_chain; const account = (label: string) => actors.get(label)!.certificate.account;
function apply(state: GroupProjection | undefined, event: GroupEvent, label = 'A') { return applyGroupEvent(state, event, actors.get(label)!.certificate, group, context); }

test('independent management chain reconstructs every projection, preserves gaps and separates relay rotation/message events from administration hashes', () => {
    let state: GroupProjection | undefined;
    for (const row of data.chain) {
        if (row.event.sequence === 8) { const rotated = applyFixtureEvent(state, data.automatic_rotation.event).projection; expect(rotated.managementHash).toBe(state!.managementHash); expect(rotated.epoch).toBe(8); state = rotated; }
        const result = applyFixtureEvent(state, row.event); expect(result.rejection).toBeUndefined(); state = result.projection;
        expect(groupProjectionWire(state)).toEqual(row.expected_projection); expect(state.state.memberCount).toBe(state.members.length);
        expect(state.members.filter(value => value.role === 'owner').map(value => value.account)).toEqual([state.state.owner]);
        if (row.management_hash) { const payload = groupManagementPayloadCodec.decode(row.event.payload!); expect(hex(groupManagementInput(payload, context))).toBe(row.signing_input_utf8_hex); expect(state.managementHash).toBe(row.management_hash); }
    }
    expect(state!.state.status).toBe('closed'); expect(() => applyFixtureEvent(state, { ...data.automatic_rotation.event, sequence: 25, epoch: 22 })).toThrow('closed');
});
test.each(data.same_value_updates.cases)('same-value operation still advances administration head: $name', row => {
    const before = groupSnapshot(data.same_value_updates.starting_event_index); const after = applyFixtureEvent(before, row.event).projection;
    expect(groupProjectionWire(after)).toEqual(row.expected_projection); expect(after.managementHash).toBe(row.expected_hash); expect(after.managementHash).not.toBe(before.managementHash);
    expect(after.state).toEqual(before.state); expect(after.members).toEqual(before.members);
    expect(() => applyFixtureEvent(after, { ...row.event, sequence: after.sequence + 1 })).toThrow('predecessor');
});
test.each(data.ban_transitions.cases)('independent ban transitions are atomic and advance epochs only when removing members: $name', row => {
    let state = groupSnapshot(data.ban_transitions.starting_event_index);
    for (const step of row.steps) { state = applyFixtureEvent(state, step.event).projection; expect(groupProjectionWire(state)).toEqual(step.expected_projection); }
});
test('ownership returning to the former owner does not revive an old signed transfer', () => {
    let state = groupSnapshot(data.ownership_round_trip.starting_event_index);
    for (const step of data.ownership_round_trip.steps) { state = applyFixtureEvent(state, step.event).projection; expect(groupProjectionWire(state)).toEqual(step.expected_projection); }
    expect(state.state.owner).toBe(account('A'));
    expect(() => applyFixtureEvent(state, { ...data.ownership_round_trip.replay_event, sequence: state.sequence + 1 })).toThrow('predecessor');
});
test('concurrent updates must be re-signed after synchronizing the new head', () => {
    const first = applyFixtureEvent(groupSnapshot(2), data.concurrency.first).projection;
    expect(() => applyFixtureEvent(first, data.concurrency.conflicting)).toThrow('predecessor');
    const second = applyFixtureEvent(first, data.concurrency.retry).projection; expect(second.managementHash).toBe(data.concurrency.retry_expected_hash);
    const unchangedSignature = groupEventCodec.decode(data.concurrency.conflicting);
    expect(() => apply(first, { ...unchangedSignature, payload: { ...unchangedSignature.payload, prev_hash: first.managementHash } })).toThrow('signature');
});
test.each(data.sync_certificate_cases)('group sync signing evidence: $name', row => {
    const page = groupSyncPageCodec.decode({ ...data.sync_response, ...(row.empty_page ? { events: [], has_more: false } : {}), certificates: row.certificate_actors.map(label => {
        const wire = data.actors.find(actor => actor.label === label)!.certificate;
        return label === row.invalidate_device_signature_of ? { ...wire, device_signature: encodeBase64Url(new Uint8Array(64)) } : wire;
    }) });
    const validate = () => validateGroupSyncPage(page, { groupId: group.groupId, after: -1 }, context);
    if (row.expected_certificate_set_valid) expect(validate).not.toThrow(); else expect(validate).toThrow();
});
test.each(data.member_requests)('independent member request verifies before approval: $label', row => {
    const certificate = actors.get(row.actor)!.certificate;
    if (row.request.$type === 'meshline.group.application') {
        const request = groupApplicationCodec.decode(row.request); verifyGroupApplication(request, certificate, context); expect(hex(groupApplicationInput(request, context))).toBe(row.signing_input_utf8_hex);
        expect(() => verifyGroupApplication({ ...request, memberEncryptionPublicKey: new Uint8Array(32) }, certificate, context)).toThrow('signature');
    } else {
        const request = groupMemberRecoveryRequestCodec.decode(row.request); verifyGroupMemberRecoveryRequest(request, certificate, context); expect(hex(groupMemberRecoveryRequestInput(request, context))).toBe(row.signing_input_utf8_hex);
        expect(() => verifyGroupMemberRecoveryRequest({ ...request, memberEncryptionPublicKey: new Uint8Array(32) }, certificate, context)).toThrow('signature');
    }
});
test.each(data.signer_account_binding.cases)('leave binds to signer account, not only Ed25519 key: $name', row => {
    const before = groupSnapshot(data.signer_account_binding.event_index - 1); const original = groupEventCodec.decode(data.chain[data.signer_account_binding.event_index]!.event);
    const event = { ...original, signerDeviceId: row.signer_device_id }; const certificate = deviceCertificateCodec.decode(row.certificate);
    const run = () => applyGroupEvent(before, event, certificate, group, context);
    if (row.expected_accepted) expect(run().projection.members.some(member => member.account === account('A'))).toBe(false); else expect(run).toThrow('authorized');
});
test('member application and recovery reject a different account certificate sharing the same signing key', () => {
    for (const request of data.member_request_account_binding.requests) for (const binding of data.signer_account_binding.cases) {
        const certificate = deviceCertificateCodec.decode(binding.certificate);
        const verify = () => request.name === 'application' ? verifyGroupApplication(groupApplicationCodec.decode(request.request), certificate, context)
            : verifyGroupMemberRecoveryRequest(groupMemberRecoveryRequestCodec.decode(request.request), certificate, context);
        if (binding.name === 'same_account') expect(verify).not.toThrow(); else expect(verify).toThrow('another account');
    }
});

const invalidOperations: { name: string; index: number; actor: string; type: string; fields: JsonObject; epochDelta?: number }[] = [
    { name: 'member cannot approve admission', index: 1, actor: 'B', type: 'application.approval', fields: { members: [{ account: account('C'), member_encryption_public_key: actors.get('C')!.member_encryption_public_key }] }, epochDelta: 2 },
    { name: 'banned administrator cannot remove members', index: 6, actor: 'B', type: 'member.removal', fields: { accounts: [account('C')] }, epochDelta: 2 },
    { name: 'former owner cannot change properties', index: 12, actor: 'A', type: 'update', fields: { name: 'forbidden' } },
    { name: 'administrator cannot remove owner', index: 2, actor: 'B', type: 'member.removal', fields: { accounts: [account('A')] }, epochDelta: 2 },
    { name: 'unknown administration type blocks projection', index: 2, actor: 'A', type: 'future_update', fields: { name: 'future' } },
    { name: 'unknown properties do not satisfy update fields', index: 2, actor: 'A', type: 'update', fields: { future_data: true } },
    { name: 'banned account cannot rejoin', index: 6, actor: 'A', type: 'application.approval', fields: { members: [{ account: account('B'), member_encryption_public_key: actors.get('B')!.member_encryption_public_key }] }, epochDelta: 2 },
    { name: 'unban alone does not restore membership', index: 7, actor: 'B', type: 'member.removal', fields: { accounts: [account('C')] }, epochDelta: 2 },
    { name: 'readmission does not restore administrator role', index: 8, actor: 'B', type: 'member.removal', fields: { accounts: [account('C')] }, epochDelta: 2 },
    { name: 'administrator cannot ban owner', index: 2, actor: 'B', type: 'member.ban', fields: { accounts: [account('A')] }, epochDelta: 2 },
    { name: 'administrator cannot ban self', index: 2, actor: 'B', type: 'member.ban', fields: { accounts: [account('B')] }, epochDelta: 2 },
    { name: 'ban requires accounts', index: 2, actor: 'A', type: 'member.ban', fields: { accounts: [] } },
    { name: 'ban account list must be unique', index: 2, actor: 'A', type: 'member.ban', fields: { accounts: [account('B'), account('B')] }, epochDelta: 2 },
    { name: 'invalid later batch target rolls back earlier removal', index: 2, actor: 'A', type: 'member.ban', fields: { accounts: [account('B'), account('A')] }, epochDelta: 2 },
    { name: 'ban of member must advance epoch', index: 2, actor: 'A', type: 'member.ban', fields: { accounts: [account('B')] } },
    { name: 'ban of nonmember cannot advance epoch', index: 2, actor: 'A', type: 'member.ban', fields: { accounts: [account('C')] }, epochDelta: 2 },
    { name: 'unban cannot advance epoch', index: 6, actor: 'A', type: 'member.unban', fields: { accounts: [account('B')] }, epochDelta: 2 },
    { name: 'leave must name signing account', index: 12, actor: 'A', type: 'member.leave', fields: { account: account('C') }, epochDelta: 2 },
    { name: 'leave account cannot be omitted', index: 12, actor: 'A', type: 'member.leave', fields: {}, epochDelta: 2 },
    { name: 'owner cannot leave without transfer', index: 2, actor: 'A', type: 'member.leave', fields: { account: account('A') }, epochDelta: 2 },
    { name: 'recovery key must change', index: 3, actor: 'A', type: 'member.recovery.approval', fields: { members: [{ account: account('C'), member_encryption_public_key: actors.get('C')!.member_encryption_public_key }] }, epochDelta: 2 },
];
test.each(invalidOperations)('management rejects atomically: $name', row => {
    const before = groupSnapshot(row.index); const saved = structuredClone(before);
    expect(() => apply(before, signedGroupEvent(before, row.actor, row.type, row.fields, before.epoch + (row.epochDelta ?? 0)), row.actor)).toThrow(); expect(before).toEqual(saved);
});
test('management chain rejects skipped, duplicate, reordered and re-created history', () => {
    const initial = groupSnapshot(0); const first = groupEventCodec.decode(data.chain[1]!.event); const second = groupEventCodec.decode(data.chain[2]!.event);
    expect(() => apply(initial, second)).toThrow('predecessor'); expect(() => apply(groupSnapshot(2), first)).toThrow('sequence');
    expect(() => applyFixtureEvent(groupSnapshot(1), data.chain[3]!.event)).toThrow('predecessor');
    expect(() => applyFixtureEvent(initial, { ...data.chain[0]!.event, sequence: 10 })).toThrow('creation');
    expect(() => apply(groupSnapshot(1), signedGroupEvent(groupSnapshot(1), 'A', 'role.update', { account: account('B'), role: 'administrator', prev_hash: initial.managementHash }))).toThrow('predecessor');
    const invalidCreate = groupEventCodec.decode(data.chain[0]!.event); const payload: JsonObject = { ...invalidCreate.payload, prev_hash: null };
    payload.device_signature = encodeBase64Url(signDevice(signingInput(payload, context, ['device_signature']), decodeBase64Url(actors.get('A')!.signing_private_key)));
    expect(() => apply(undefined, { ...invalidCreate, payload })).toThrow('Null');
});
test('management extensions stay signed but cannot replace projected roles or accounts', () => {
    const before = groupSnapshot(8); const changed = groupEventCodec.decode(data.chain[9]!.event);
    expect(() => apply(before, { ...changed, payload: { ...changed.payload, future_data: false } })).toThrow('signature');
    const update = signedGroupEvent(before, 'A', 'update', { description: null, member_capacity: 1, owner: account('C'), members: [], name: 'partial update' });
    const after = apply(before, update).projection; expect(after.state.description).toBeUndefined(); expect(after.state.memberCapacity).toBe(1); expect(after.members).toEqual(before.members); expect(after.state.owner).toBe(account('A'));
    const invitation = signedGroupEvent(after, 'A', 'application.approval', { members: [{ account: account('C'), member_encryption_public_key: actors.get('C')!.member_encryption_public_key }] }, after.epoch + 1);
    expect(() => apply(after, invitation)).toThrow('capacity');
});
test('invalid ordinary message is explicit and advances only its sequence; structural epoch/certificate failures block synchronization', () => {
    const before = groupSnapshot(13); const entry = groupEventCodec.decode(data.chain[14]!.event);
    const malformed = { ...entry, payload: { ...entry.payload, device_signature: encodeBase64Url(new Uint8Array(64)) } };
    const rejected = apply(before, malformed, 'B'); expect(rejected.rejection?.code).toBe('invalid_signature'); expect(rejected.message).toBeUndefined();
    expect(rejected.projection.sequence).toBe(entry.sequence); expect(rejected.projection.managementHash).toBe(before.managementHash); expect(rejected.projection.members).toEqual(before.members);
    expect(() => apply(before, { ...entry, epoch: entry.epoch + 1 }, 'B')).toThrow('epoch');
    expect(() => applyGroupEvent(before, entry, undefined, group, context)).toThrow('certificate');
    expect(() => groupManagementPayloadCodec.decode({ $type: '__proto__' })).toThrow('Unknown');
});
test('invitations validate account signature binding, lifetime, use limits and previews do not allow null', () => {
    const actor = actors.get('A')!;
    let invite = groupInviteCodec.decode({ $type: 'meshline.group.invite', invite_id: 'inv_AAECAwQFBgcICQoLDA0ODw', group_id: group.groupId, inviter: account('A'), created_at: 1730000000, expires_at: 1730001000, max_uses: 2, device_signature: encodeBase64Url(new Uint8Array(64)) });
    invite = { ...invite, deviceSignature: signDevice(groupInviteInput(invite, context), decodeBase64Url(actor.signing_private_key)) }; verifyGroupInvite(invite, actor.certificate, context, 1730000010);
    expect(() => validateGroupInvite(invite, 1730001000)).toThrow('expire'); expect(() => validateGroupInvite({ ...invite, invitee: account('B') }, 1730000010)).toThrow('shareable');
    const wrong = deviceCertificateCodec.decode(data.signer_account_binding.cases.find(value => value.name === 'different_account')!.certificate);
    expect(() => verifyGroupInvite(invite, wrong, context, 1730000010)).toThrow('another account');
    const state = groupSnapshot(2).state; validateGroupState(state); expect(() => groupStateCodec.decode({ ...groupStateCodec.encode(state), description: null })).toThrow();
    expect(certificateId(actor.certificate, context)).toBe(actor.device_id);
});
