import { expect, test } from 'vitest';
import {
    NetworkContext, contactGrantCodec, contactGrantInput, contactInviteCodec, contactInviteInput, decodeBase64Url, deviceCertificateCodec,
    signDevice, signingInput, validateContactGrant, verifyDevice, validateAccountContactSync, accountContactSyncCodec, type JsonObject,
    selectContactGrant, signAccount, deviceStateInput, type AccountDeviceState,
} from '@meshline/sdk';
import { hex, vector } from '../support/vectors.js';

const data = vector<{
    network_context: string;
    signing: { vector: { grant: JsonObject; private_key: string; public_key: string; signature: string; signing_input_utf8_hex: string };
        verification_cases: { name: string; grant_overrides?: JsonObject; grant_remove_fields?: string[]; network_context?: string; public_key?: string; signature?: string; expected_signature_valid: boolean }[] };
    grants: { verification_time: number; format_cases: { name: string; input_json: string; expected: string }[];
        signing_devices: { label: string; signing_private_key: string; certificate: JsonObject; device_id: string }[];
        current_valid_devices: string[]; grants: { label: string; grant: JsonObject }[];
        selection_cases: { name: string; current: string; candidate: string; candidate_overrides?: JsonObject; candidate_network_context?: string;
            current_valid_devices?: string[]; expected_action: string; expected_selected: string }[] };
    invites: { invites: { label: string; object: JsonObject; signing_input_utf8_hex: string }[];
        verification_cases: { name: string; invite: string; invite_overrides?: JsonObject; network_context?: string; expected_signature_valid: boolean; expected_type_supported: boolean }[] };
}>('contacts');
const context = NetworkContext.parse(data.network_context);
test('contact grant independent signature vector retains every signed extension', () => {
    const row = data.signing.vector; const grant = contactGrantCodec.decode(row.grant); const input = contactGrantInput(grant, context);
    expect(hex(input)).toBe(row.signing_input_utf8_hex); expect(signDevice(input, decodeBase64Url(row.private_key))).toEqual(decodeBase64Url(row.signature));
});
test.each(data.signing.verification_cases)('contact signature scope: $name', row => {
    const fixture = data.signing.vector; const document = { ...fixture.grant, ...row.grant_overrides };
    for (const name of row.grant_remove_fields ?? []) delete document[name];
    const input = signingInput(document, row.network_context ? NetworkContext.parse(row.network_context) : context, ['signatures']);
    expect(verifyDevice(input, decodeBase64Url(row.signature ?? fixture.signature), decodeBase64Url(row.public_key ?? fixture.public_key))).toBe(row.expected_signature_valid);
});
test.each(data.grants.format_cases)('grant format: $name', row => {
    const validate = () => validateContactGrant(contactGrantCodec.parse(row.input_json), data.grants.verification_time);
    if (row.expected === 'accept') validate(); else expect(validate).toThrow();
});
test.each(data.grants.selection_cases)('grant selection: $name', row => {
    const current = contactGrantCodec.decode(data.grants.grants.find(value => value.label === row.current)!.grant);
    const candidate = contactGrantCodec.decode({ ...data.grants.grants.find(value => value.label === row.candidate)!.grant, ...row.candidate_overrides });
    const labels = row.current_valid_devices ?? data.grants.current_valid_devices;
    const certificates = data.grants.signing_devices.filter(value => labels.includes(value.label)).map(value => deviceCertificateCodec.decode(value.certificate)).filter(value => value.account === current.grantor);
    const identity = vector<{ device_certificate: { private_key: string; unsigned_object: JsonObject } }>('identity-auth').device_certificate;
    let state: AccountDeviceState = { account: current.grantor, accountPublicKey: decodeBase64Url(identity.unsigned_object.account_public_key as string), revision: 1, certificates, accountSignature: new Uint8Array(64) };
    state = { ...state, accountSignature: signAccount(deviceStateInput(state, context), decodeBase64Url(identity.private_key)) };
    const result = selectContactGrant(current, candidate, state, context, data.grants.verification_time, row.candidate_network_context ? NetworkContext.parse(row.candidate_network_context) : context);
    expect(result.action).toBe(row.expected_action);
    expect(contactGrantCodec.encode(result.grant!)).toEqual(data.grants.grants.find(value => value.label === row.expected_selected)!.grant);
});
test.each(data.invites.verification_cases)('invitation independent signature and type: $name', row => {
    const fixture = data.invites.invites.find(value => value.label === row.invite)!;
    const document = { ...fixture.object, ...row.invite_overrides };
    const network = row.network_context ? NetworkContext.parse(row.network_context) : context;
    const input = signingInput(document, network, ['device_signature']);
    const key = deviceCertificateCodec.decode(data.grants.signing_devices[0]!.certificate).signingPublicKey;
    expect(verifyDevice(input, decodeBase64Url(document.device_signature as string), key)).toBe(row.expected_signature_valid);
    if (row.expected_type_supported) expect(contactInviteInput(contactInviteCodec.decode(document), network)).toEqual(input);
    else expect(() => contactInviteCodec.decode(document)).toThrow();
});
test('contact sync enforces duplicate, account-direction, owner and deletion constraints', () => {
    const from = data.signing.vector.grant.grantor as string; const to = data.signing.vector.grant.grantee as string;
    const grant = { ...data.signing.vector.grant, signatures: { dev_AAECAwQFBgcICQoLDA0ODw: data.signing.vector.signature } };
    const record = { account: from, status: 'active', updated_at: data.grants.verification_time, grant_from_contact: grant };
    const validate = (records: JsonObject[], owner?: string) => validateAccountContactSync(accountContactSyncCodec.decode({ $type: 'meshline.account.contacts.sync', records }), data.grants.verification_time, owner);
    validate([record], to);
    expect(() => validate([record, record], to)).toThrow('duplicate');
    expect(() => validate([{ ...record, status: 'deleted' }], to)).toThrow('Deleted');
    expect(() => validate([record], from)).toThrow('accounts');
    expect(() => validate([{ account: to, status: 'active', updated_at: data.grants.verification_time }], to)).toThrow('owner');
    expect(() => validate([{ ...record, updated_at: data.grants.verification_time + 301 }], to)).toThrow('future');
});
