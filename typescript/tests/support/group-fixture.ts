import { NetworkContext, certificateId, deviceCertificateCodec, groupEventCodec, encodeBase64Url, decodeBase64Url, signDevice, signingInput,
    type JsonObject, type DeviceCertificate, type GroupEvent } from '@meshline/sdk';
import { applyGroupEvent, type GroupProjection } from '../../packages/sdk/dist/groups/state.js';
import { vector } from './vectors.js';

export interface GroupVectorStep { event: JsonObject; management_hash?: string; expected_hash?: string; expected_projection: JsonObject; signing_input_utf8_hex?: string }
export const groupVectors = vector<{ network_context: string; keying: { input: { group_id: string } }; management_chain: {
    group_id: string; relay_id: string; actors: { label: string; certificate: JsonObject; device_id: string; signing_private_key: string; member_encryption_public_key: string }[];
    chain: GroupVectorStep[]; automatic_rotation: { event: JsonObject }; sync_response: JsonObject;
    same_value_updates: { starting_event_index: number; cases: (GroupVectorStep & { name: string })[] };
    ownership_round_trip: { starting_event_index: number; steps: GroupVectorStep[]; replay_event: JsonObject };
    ban_transitions: { starting_event_index: number; cases: { name: string; steps: GroupVectorStep[] }[] };
    concurrency: { starting_hash: string; first: JsonObject; conflicting: JsonObject; retry: JsonObject; retry_expected_hash: string };
    signer_account_binding: { event_index: number; cases: { name: string; certificate: JsonObject; signer_device_id: string; expected_accepted: boolean }[] };
    member_requests: { label: string; actor: string; request: JsonObject; signing_input_utf8_hex: string }[];
    member_request_account_binding: { requests: { name: string; request: JsonObject }[]; cases: { name: string; certificate_source: string; session_account: string; request_account?: string; expected_signature_valid: boolean; expected_account_binding_valid: boolean }[] };
    sync_certificate_cases: { name: string; empty_page?: boolean; certificate_actors: string[]; invalidate_device_signature_of?: string; expected_certificate_set_valid: boolean }[];
} }>('groups');
export const groupContext = NetworkContext.parse(groupVectors.network_context);
export const groupReference = { groupId: groupVectors.management_chain.group_id, relayId: groupVectors.management_chain.relay_id };
export const groupActors = new Map(groupVectors.management_chain.actors.map(row => [row.label, { ...row, certificate: deviceCertificateCodec.decode(row.certificate) }]));
const certificates = new Map<string, DeviceCertificate>([...groupActors.values()].map(actor => [actor.device_id, actor.certificate]));
export function applyFixtureEvent(previous: GroupProjection | undefined, wire: JsonObject) {
    const entry = groupEventCodec.decode(wire); return applyGroupEvent(previous, entry, entry.signerDeviceId === undefined ? undefined : certificates.get(entry.signerDeviceId), groupReference, groupContext);
}
export function groupSnapshot(index: number): GroupProjection {
    let state: GroupProjection | undefined;
    for (const row of groupVectors.management_chain.chain.slice(0, index + 1)) {
        if (row.event.sequence === 8) state = applyFixtureEvent(state, groupVectors.management_chain.automatic_rotation.event).projection;
        state = applyFixtureEvent(state, row.event).projection;
    }
    if (!state) throw new Error('Missing fixture group state'); return state;
}
export function groupProjectionWire(value: GroupProjection): JsonObject {
    return { head: value.managementHash, status: value.state.status, members: Object.fromEntries(value.members.map(member => [member.account, { account: member.account, role: member.role, member_encryption_public_key: encodeBase64Url(member.memberEncryptionPublicKey) }])),
        bans: [...value.bans], name: value.state.name, ...(value.state.description === undefined ? {} : { description: value.state.description }), member_capacity: value.state.memberCapacity, invite_policy: value.state.invitePolicy, client_secret_commitment: value.clientSecretCommitment };
}
export function signedGroupEvent(state: GroupProjection, actorLabel: string, type: string, fields: JsonObject = {}, epoch = state.epoch): GroupEvent {
    const actor = groupActors.get(actorLabel)!;
    const wire = { $type: `meshline.group.${type}`, group_id: state.groupId, prev_hash: state.managementHash, ...fields };
    return { sequence: state.sequence + 1, epoch, acceptedAt: 1730000100, signerDeviceId: certificateId(actor.certificate, groupContext),
        payload: { ...wire, device_signature: encodeBase64Url(signDevice(signingInput(wire, groupContext, ['device_signature']), decodeBase64Url(actor.signing_private_key))) } };
}
