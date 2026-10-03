import { ProtocolError } from '../errors.js';
import { bytes, defineCodec, enumeration, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import { NetworkContext, signingInput } from '../protocol/context.js';
import { encodeBase64Url } from '../protocol/encoding.js';
import { requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { validateRelayId } from '../identity/neo.js';
import { relayOrigin } from '../transport/endpoint.js';
import { certificateId, deviceCertificateCodec, type DeviceCertificate } from './identity.js';

export type SessionMode = 'account' | 'device';
export interface AuthenticationChallenge extends ExtensibleModel { readonly nonce: string; readonly createdAt: number; readonly expiresAt: number }
/** The token is a credential: never include this object in diagnostic logs. */
export interface SessionCredentials extends ExtensibleModel { readonly token: string; readonly mode: SessionMode; readonly expiresAt: number }
export interface AccountAuthenticationRequest extends ExtensibleModel { readonly nonce: string; readonly accountPublicKey: Uint8Array; readonly accountSignature: Uint8Array }
export interface DeviceAuthenticationRequest extends ExtensibleModel { readonly nonce: string; readonly timestamp: number; readonly signerCertificate: DeviceCertificate; readonly deviceSignature: Uint8Array }

export const authenticationChallengeCodec = defineCodec<AuthenticationChallenge>({
    nonce: { wire: 'nonce', codec: text }, createdAt: { wire: 'created_at', codec: integer }, expiresAt: { wire: 'expires_at', codec: integer },
});
export const sessionCredentialsCodec = defineCodec<SessionCredentials>({
    token: { wire: 'token', codec: text }, mode: { wire: 'mode', codec: enumeration('account', 'device') }, expiresAt: { wire: 'expires_at', codec: integer },
});
export const accountAuthenticationCodec = defineCodec<AccountAuthenticationRequest>({
    nonce: { wire: 'nonce', codec: text }, accountPublicKey: { wire: 'account_public_key', codec: bytes }, accountSignature: { wire: 'account_signature', codec: bytes },
});
export const deviceAuthenticationCodec = defineCodec<DeviceAuthenticationRequest>({
    nonce: { wire: 'nonce', codec: text }, timestamp: { wire: 'timestamp', codec: integer },
    signerCertificate: { wire: 'signer_certificate', codec: deviceCertificateCodec }, deviceSignature: { wire: 'device_signature', codec: bytes },
});

export function validateAuthToken(value: string): void {
    if (typeof value !== 'string' || value.length < 1 || value.length > 256 || /[^\x21-\x7e]/.test(value))
        throw new ProtocolError('invalid_auth_token', 'Authentication tokens must contain 1 to 256 visible ASCII characters.');
}

export function validateChallenge(challenge: AuthenticationChallenge): void {
    validateCompleteObject(authenticationChallengeCodec.encode(challenge));
    validateAuthToken(challenge.nonce);
    requireSafeInteger(challenge.createdAt, 0);
    requireSafeInteger(challenge.expiresAt, 0);
    if (challenge.expiresAt <= challenge.createdAt) throw new ProtocolError('invalid_time', 'Challenge expiry must follow its creation time.');
}

export function validateSession(credentials: SessionCredentials): void {
    validateCompleteObject(sessionCredentialsCodec.encode(credentials));
    validateAuthToken(credentials.token);
    requireSafeInteger(credentials.expiresAt, 1);
    if (credentials.mode !== 'account' && credentials.mode !== 'device') throw new ProtocolError('invalid_mode', 'Invalid session mode.');
}

/** Method-defined projection: request extensions cannot override the trusted target. */
export function accountAuthenticationInput(request: AccountAuthenticationRequest, account: string, relayId: string, endpoint: string, context: NetworkContext): Uint8Array {
    validateRelayId(relayId);
    return signingInput({ $type: 'meshline.relay.account_auth', relay_id: relayId, origin: relayOrigin(endpoint),
        account, account_public_key: encodeBase64Url(request.accountPublicKey), nonce: request.nonce }, context, []);
}

export function deviceAuthenticationInput(request: DeviceAuthenticationRequest, relayId: string, endpoint: string, context: NetworkContext): Uint8Array {
    validateRelayId(relayId);
    return signingInput({ $type: 'meshline.relay.auth', relay_id: relayId, origin: relayOrigin(endpoint), account: request.signerCertificate.account,
        device_id: certificateId(request.signerCertificate, context), nonce: request.nonce, timestamp: request.timestamp }, context, []);
}
