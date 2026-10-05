import { describe, expect, test } from 'vitest';
import {
    NetworkContext, accountPublicKey, decodeBase64Url, deriveDeviceId, deriveResourceId,
    devicePublicKey, getAccountId, getRelayId, matchesAccount, nextRevision, requireObject,
    signAccount, signDevice, signingInput, validateAccountId, verifyAccount, verifyDevice, verifyRelay,
    type JsonObject,
} from '@meshline/sdk';
import { hex, vector } from '../support/vectors.js';

interface IdentityVectors {
    network_context: string;
    device_identity: { derived_device_id: string };
    device_certificate: {
        private_key: string; public_key: string; account: string;
        device_private_key: string; device_public_key: string; unsigned_object: JsonObject;
        device_signing_input_utf8_hex: string; account_signing_input_utf8_hex: string;
        device_signature: string; account_signature: string;
    };
}

const identity = vector<IdentityVectors>('identity-auth');
const context = NetworkContext.parse(identity.network_context);
const certificate = identity.device_certificate;
const publicKey = decodeBase64Url(certificate.public_key);
const deviceKey = decodeBase64Url(certificate.device_public_key);

test('Neo account identity and public keys match independent fixtures', () => {
    expect(accountPublicKey(decodeBase64Url(certificate.private_key))).toEqual(publicKey);
    expect(devicePublicKey(decodeBase64Url(certificate.device_private_key))).toEqual(deviceKey);
    expect(getAccountId(`neo:${context.reference}`, publicKey)).toBe(certificate.account);
    expect(matchesAccount(certificate.account, publicKey)).toBe(true);
    validateAccountId(certificate.account);
    expect(() => validateAccountId(certificate.account.slice(0, -1) + '1')).toThrow();
    expect(() => validateAccountId(certificate.account + '\n')).toThrow();
});

test('device identity excludes certificate validity and signature fields', () => {
    expect(deriveDeviceId(certificate.account, deviceKey,
        decodeBase64Url(certificate.unsigned_object.encryption_public_key as string), context))
        .toBe(identity.device_identity.derived_device_id);
});

test('certificate nested signatures preserve the supplied signature bytes', () => {
    const document = { ...certificate.unsigned_object, device_signature: certificate.device_signature, account_signature: certificate.account_signature };
    const deviceInput = signingInput(document, context, ['device_signature', 'account_signature']);
    expect(hex(deviceInput)).toBe(certificate.device_signing_input_utf8_hex);
    expect(signDevice(deviceInput, decodeBase64Url(certificate.device_private_key))).toEqual(decodeBase64Url(certificate.device_signature));
    expect(verifyDevice(deviceInput, decodeBase64Url(certificate.device_signature), deviceKey)).toBe(true);

    const accountInput = signingInput(document, context, ['account_signature']);
    expect(hex(accountInput)).toBe(certificate.account_signing_input_utf8_hex);
    expect(verifyAccount(accountInput, decodeBase64Url(certificate.account_signature), publicKey)).toBe(true);
    expect(verifyAccount(accountInput, signAccount(accountInput, decodeBase64Url(certificate.private_key)), publicKey)).toBe(true);

    const tampered = new Uint8Array(accountInput);
    tampered[0] = tampered[0]! ^ 1;
    expect(verifyAccount(tampered, decodeBase64Url(certificate.account_signature), publicKey)).toBe(false);
    const otherNetwork = new NetworkContext(context.reference + 1, context.registry);
    expect(verifyDevice(signingInput(document, otherNetwork, ['device_signature', 'account_signature']), decodeBase64Url(certificate.device_signature), deviceKey)).toBe(false);
});

describe('resource identities', () => {
    test.each(['channel', 'group'] as const)('%s identity matches fixed vectors', kind => {
        const source = vector<Record<string, JsonObject>>(kind === 'channel' ? 'channels' : 'groups');
        const row = source[`${kind}_id`]!;
        const input = requireObject(row.input!);
        const expected = requireObject(row.expected!);
        expect(deriveResourceId(kind, input.creator as string, input.relay_id as string,
            decodeBase64Url(input.nonce as string), context)).toBe(expected[`${kind}_id`]);
    });
});

test('relay signature verification binds the key to the expected relay', () => {
    const input = new Uint8Array([1, 2, 3]);
    const signature = signAccount(input, decodeBase64Url(certificate.private_key));
    expect(verifyRelay(getRelayId(publicKey), publicKey, input, signature)).toBe(true);
    expect(verifyRelay('0x' + '00'.repeat(20), publicKey, input, signature)).toBe(false);
});

test.each([[-1, 0], [0, 1], [Number.MAX_SAFE_INTEGER - 1, Number.MAX_SAFE_INTEGER]])('revision %i advances to %i', (previous, expected) => {
    expect(nextRevision(previous!)).toBe(expected);
});

test.each([[4, 4], [4, 3], [4, -1], [4, Number.MAX_SAFE_INTEGER + 1], [Number.MAX_SAFE_INTEGER, undefined]])('rejects unsafe revision known=%i requested=%s', (known, requested) => {
    expect(() => nextRevision(known!, requested)).toThrow();
});
