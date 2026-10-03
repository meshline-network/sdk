import { requireLength, verifyAccount, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { deriveDeviceId, validateIdentifier } from '../identity/identifiers.js';
import { matchesAccount, validateRelayId } from '../identity/neo.js';
import { array, bytes, defineCodec, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import { NetworkContext, signingInput } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { canonicalJson, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';

export interface DeviceCertificate extends ExtensibleModel {
    readonly account: string;
    readonly accountPublicKey: Uint8Array;
    readonly signingPublicKey: Uint8Array;
    readonly encryptionPublicKey: Uint8Array;
    readonly notBefore: number;
    readonly expiresAt: number;
    readonly deviceSignature: Uint8Array;
    readonly accountSignature: Uint8Array;
}

export const deviceCertificateCodec = defineCodec<DeviceCertificate>({
    account: { wire: 'account', codec: text },
    accountPublicKey: { wire: 'account_public_key', codec: bytes },
    signingPublicKey: { wire: 'signing_public_key', codec: bytes },
    encryptionPublicKey: { wire: 'encryption_public_key', codec: bytes },
    notBefore: { wire: 'not_before', codec: integer },
    expiresAt: { wire: 'expires_at', codec: integer },
    deviceSignature: { wire: 'device_signature', codec: bytes },
    accountSignature: { wire: 'account_signature', codec: bytes },
}, 'meshline.device.certificate');

export function certificateId(certificate: DeviceCertificate, context: NetworkContext): string {
    return deriveDeviceId(certificate.account, certificate.signingPublicKey, certificate.encryptionPublicKey, context);
}

export function certificateDeviceInput(certificate: DeviceCertificate, context: NetworkContext): Uint8Array {
    return signingInput(deviceCertificateCodec.encode(certificate), context, ['device_signature', 'account_signature']);
}

export function certificateAccountInput(certificate: DeviceCertificate, context: NetworkContext): Uint8Array {
    return signingInput(deviceCertificateCodec.encode(certificate), context, ['account_signature']);
}

/** Checks complete certificate integrity; current account authorization is a separate operation. */
export function validateCertificate(certificate: DeviceCertificate, context: NetworkContext): void {
    validateCompleteObject(deviceCertificateCodec.encode(certificate));
    requireLength(certificate.signingPublicKey, 32, 'Signing public key');
    requireLength(certificate.encryptionPublicKey, 32, 'Encryption public key');
    requireLength(certificate.deviceSignature, 64, 'Device signature');
    requireLength(certificate.accountSignature, 64, 'Account signature');
    requireSafeInteger(certificate.notBefore, 0);
    requireSafeInteger(certificate.expiresAt, 0);
    if (certificate.expiresAt <= certificate.notBefore || certificate.expiresAt - certificate.notBefore > 720 * 86400)
        throw new ProtocolError('invalid_time', 'Certificate validity must be positive and at most 720 days.');
    if (!matchesAccount(certificate.account, certificate.accountPublicKey)) throw new ProtocolError('invalid_identity', 'Certificate account does not match its public key.');
    if (encodeUtf8(deviceCertificateCodec.stringify(certificate)).length > 4096) throw new ProtocolError('invalid_size', 'Certificate exceeds 4096 bytes.');
    if (!verifyDevice(certificateDeviceInput(certificate, context), certificate.deviceSignature, certificate.signingPublicKey))
        throw new ProtocolError('invalid_signature', 'Invalid certificate device signature.');
    if (!verifyAccount(certificateAccountInput(certificate, context), certificate.accountSignature, certificate.accountPublicKey))
        throw new ProtocolError('invalid_signature', 'Invalid certificate account signature.');
}

export interface AccountDeviceState extends ExtensibleModel {
    readonly account: string;
    readonly accountPublicKey: Uint8Array;
    readonly revision: number;
    readonly certificates: readonly DeviceCertificate[];
    readonly accountSignature: Uint8Array;
}

export const accountDeviceStateCodec = defineCodec<AccountDeviceState>({
    account: { wire: 'account', codec: text }, accountPublicKey: { wire: 'account_public_key', codec: bytes },
    revision: { wire: 'revision', codec: integer }, certificates: { wire: 'certificates', codec: array(deviceCertificateCodec) },
    accountSignature: { wire: 'account_signature', codec: bytes },
}, 'meshline.account.device.state');

export function deviceStateInput(state: AccountDeviceState, context: NetworkContext): Uint8Array {
    return signingInput(accountDeviceStateCodec.encode(state), context, ['account_signature']);
}

export function validateDeviceState(state: AccountDeviceState, context: NetworkContext): void {
    validateCompleteObject(accountDeviceStateCodec.encode(state));
    requireSafeInteger(state.revision, 0);
    if (!matchesAccount(state.account, state.accountPublicKey)) throw new ProtocolError('invalid_identity', 'Device state account does not match its key.');
    if (state.certificates.length > 8) throw new ProtocolError('invalid_size', 'At most eight devices may be authorized.');
    if (encodeUtf8(accountDeviceStateCodec.stringify(state)).length > 131072) throw new ProtocolError('invalid_size', 'Device state exceeds 131072 bytes.');
    const identities = new Set<string>();
    for (const certificate of state.certificates) {
        validateCertificate(certificate, context);
        if (certificate.account !== state.account) throw new ProtocolError('invalid_identity', 'Certificate belongs to another account.');
        const id = certificateId(certificate, context);
        if (identities.has(id)) throw new ProtocolError('duplicate_device', 'Duplicate device identity.');
        identities.add(id);
    }
    if (!verifyAccount(deviceStateInput(state, context), state.accountSignature, state.accountPublicKey))
        throw new ProtocolError('invalid_signature', 'Invalid device state signature.');
}

/** The state must have passed validateDeviceState before use as trusted authorization. */
export function authorizedDevice(state: AccountDeviceState, deviceId: string, context: NetworkContext, now: number): DeviceCertificate {
    requireSafeInteger(now, 0);
    validateIdentifier('device', deviceId);
    const certificate = state.certificates.find(item => certificateId(item, context) === deviceId);
    if (!certificate) throw new ProtocolError('unauthorized_device', 'Device is not registered in the account state.');
    if (now < certificate.notBefore || now >= certificate.expiresAt) throw new ProtocolError('unauthorized_device', 'Device is outside its authorization window.');
    return certificate;
}

export interface AccountRoute extends ExtensibleModel {
    readonly account: string;
    readonly accountPublicKey: Uint8Array;
    readonly revision: number;
    readonly relayId: string;
    readonly updatedAt: number;
    readonly expiresAt: number;
    readonly accountSignature: Uint8Array;
    readonly relaySignature?: Uint8Array;
}

export const accountRouteCodec = defineCodec<AccountRoute>({
    account: { wire: 'account', codec: text }, accountPublicKey: { wire: 'account_public_key', codec: bytes },
    revision: { wire: 'revision', codec: integer }, relayId: { wire: 'relay_id', codec: text },
    updatedAt: { wire: 'updated_at', codec: integer }, expiresAt: { wire: 'expires_at', codec: integer },
    accountSignature: { wire: 'account_signature', codec: bytes }, relaySignature: { wire: 'relay_signature', codec: bytes, optional: true },
}, 'meshline.account.route');

export function routeAccountInput(route: AccountRoute, context: NetworkContext): Uint8Array {
    return signingInput(accountRouteCodec.encode(route), context, ['account_signature', 'relay_signature']);
}

export function routeRelayInput(route: AccountRoute, context: NetworkContext): Uint8Array {
    return signingInput(accountRouteCodec.encode(route), context, ['relay_signature']);
}

/** Relay acknowledgement, when present, must additionally be checked against the relay key. */
export function validateRoute(route: AccountRoute, context: NetworkContext, now: number): void {
    validateCompleteObject(accountRouteCodec.encode(route));
    requireSafeInteger(now, 0);
    requireSafeInteger(route.revision, 0);
    requireSafeInteger(route.updatedAt, 0);
    requireSafeInteger(route.expiresAt, 0);
    validateRelayId(route.relayId);
    if (route.expiresAt <= route.updatedAt || route.expiresAt - route.updatedAt > 315_360_000 || route.expiresAt <= now)
        throw new ProtocolError('invalid_time', 'Route is expired or has an invalid validity window.');
    if (!matchesAccount(route.account, route.accountPublicKey)) throw new ProtocolError('invalid_identity', 'Route account does not match its key.');
    if (route.relaySignature !== undefined) requireLength(route.relaySignature, 64, 'Relay signature');
    if (encodeUtf8(accountRouteCodec.stringify(route)).length > 4096) throw new ProtocolError('invalid_size', 'Route exceeds 4096 bytes.');
    if (!verifyAccount(routeAccountInput(route, context), route.accountSignature, route.accountPublicKey))
        throw new ProtocolError('invalid_signature', 'Invalid account route signature.');
}

export function compareRoutes(left: AccountRoute, right: AccountRoute): 'older' | 'newer' | 'equivalent' | 'conflict' {
    if (left.account !== right.account) throw new ProtocolError('invalid_identity', 'Routes belong to different accounts.');
    if (left.revision < right.revision) return 'older';
    if (left.revision > right.revision) return 'newer';
    const unsigned = (value: AccountRoute): JsonObject => {
        const object = accountRouteCodec.encode(value);
        delete object.account_signature;
        delete object.relay_signature;
        return object;
    };
    return canonicalJson(unsigned(left)) === canonicalJson(unsigned(right)) ? 'equivalent' : 'conflict';
}
