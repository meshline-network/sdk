import { expect, test } from 'vitest';
import {
    NetworkContext, accountDeviceStateCodec, accountRouteCodec, authorizedDevice, certificateAccountInput,
    certificateDeviceInput, certificateId, compareRoutes, decodeBase64Url, deviceCertificateCodec,
    deviceStateInput, parseJson, requireObject, routeAccountInput, signAccount, signDevice,
    validateCertificate, validateDeviceState, validateRoute, type AccountDeviceState, type AccountRoute, type JsonObject,
} from '@meshline/sdk';
import { vector } from '../support/vectors.js';

interface Fixture {
    network_context: string;
    device_certificate: { unsigned_object: JsonObject; device_signature: string; account_signature: string; private_key: string; device_private_key: string };
}
const fixture = vector<Fixture>('identity-auth');
const context = NetworkContext.parse(fixture.network_context);
const row = fixture.device_certificate;
const wire: JsonObject = { ...row.unsigned_object, device_signature: row.device_signature, account_signature: row.account_signature };
const certificate = deviceCertificateCodec.decode(wire);
const privateKey = decodeBase64Url(row.private_key);

test('certificate codec maps camelCase and validates fixed signatures', () => {
    validateCertificate(certificate, context);
    expect(deviceCertificateCodec.encode(certificate)).toEqual(wire);
    expect(certificate.notBefore).toBe(wire.not_before);
});

test('unknown signed fields remain covered through decoding and encoding', () => {
    let updated = deviceCertificateCodec.decode({ ...wire, future: { signature: 'nested', values: [3, 2, 1] } });
    expect(() => validateCertificate(updated, context)).toThrow('signature');
    updated = { ...updated, deviceSignature: signDevice(certificateDeviceInput(updated, context), decodeBase64Url(row.device_private_key)) };
    updated = { ...updated, accountSignature: signAccount(certificateAccountInput(updated, context), privateKey) };
    validateCertificate(updated, context);
    const roundtrip = deviceCertificateCodec.parse(deviceCertificateCodec.stringify(updated));
    validateCertificate(roundtrip, context);
    expect(roundtrip.additionalProperties).toEqual({ future: { signature: 'nested', values: [3, 2, 1] } });
    expect(() => deviceCertificateCodec.encode({ ...certificate, additionalProperties: { account: 'override' } })).toThrow('conflicts');
    expect(() => deviceCertificateCodec.encode({ ...certificate, additionalProperties: { $type: 'override' } })).toThrow('conflicts');
});

test.each(['account', 'account_public_key', 'signing_public_key', 'not_before', '$type'])('rejects absent or null required field %s', name => {
    const missing: JsonObject = { ...wire };
    delete missing[name];
    expect(() => deviceCertificateCodec.decode(missing)).toThrow();
    expect(() => deviceCertificateCodec.decode({ ...wire, [name]: null })).toThrow();
});

function signedState(certificates = [certificate]): AccountDeviceState {
    const state: AccountDeviceState = { account: certificate.account, accountPublicKey: certificate.accountPublicKey, revision: 0, certificates, accountSignature: new Uint8Array(64) };
    return { ...state, accountSignature: signAccount(deviceStateInput(state, context), privateKey) };
}

test('authorization is separate from structural certificate validity and uses a half-open window', () => {
    const state = signedState();
    validateDeviceState(state, context);
    expect(accountDeviceStateCodec.parse(accountDeviceStateCodec.stringify(state))).toEqual(state);
    const id = certificateId(certificate, context);
    expect(authorizedDevice(state, id, context, certificate.notBefore)).toBe(certificate);
    expect(() => authorizedDevice(state, id, context, certificate.notBefore - 1)).toThrow();
    expect(() => authorizedDevice(state, id, context, certificate.expiresAt)).toThrow();
    expect(() => validateDeviceState(signedState([certificate, certificate]), context)).toThrow('Duplicate');
    validateDeviceState(signedState([]), context);
});

function signedRoute(): AccountRoute {
    const route: AccountRoute = { account: certificate.account, accountPublicKey: certificate.accountPublicKey, revision: 0,
        relayId: '0x' + '12'.repeat(20), updatedAt: 1730000000, expiresAt: 1730001000, accountSignature: new Uint8Array(64) };
    return { ...route, accountSignature: signAccount(routeAccountInput(route, context), privateKey) };
}

test('route validation and same-revision conflicts preserve unsigned fields', () => {
    const route = signedRoute();
    validateRoute(route, context, route.updatedAt);
    expect(() => validateRoute(route, context, route.expiresAt)).toThrow('expired');
    expect(() => accountRouteCodec.decode({ ...accountRouteCodec.encode(route), relay_signature: null })).toThrow();
    expect(compareRoutes(route, { ...route, accountSignature: new Uint8Array(64) })).toBe('equivalent');
    expect(compareRoutes(route, { ...route, additionalProperties: { future: 1 } })).toBe('conflict');
    expect(compareRoutes(route, { ...route, revision: 1 })).toBe('older');
    expect(compareRoutes({ ...route, revision: 1 }, route)).toBe('newer');
    expect(requireObject(parseJson(accountRouteCodec.stringify(route))).relay_signature).toBeUndefined();
});
