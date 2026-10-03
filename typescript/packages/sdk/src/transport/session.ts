import { createAbortController } from '../runtime/abort.js';
import { verifyAccount, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { matchesAccount, validateRelayId } from '../identity/neo.js';
import type { AccountSigner, DeviceSigner } from '../interactions.js';
import {
    accountAuthenticationCodec, accountAuthenticationInput, authenticationChallengeCodec, deviceAuthenticationCodec,
    deviceAuthenticationInput, sessionCredentialsCodec, validateChallenge, validateSession,
    type AccountAuthenticationRequest, type DeviceAuthenticationRequest, type SessionCredentials, type SessionMode,
} from '../models/authentication.js';
import { certificateId, deviceCertificateCodec, validateCertificate } from '../models/identity.js';
import { NetworkContext } from '../protocol/context.js';
import { equalBytes } from '../protocol/encoding.js';
import type { JsonObject, JsonValue } from '../protocol/json.js';
import { abortScope, awaitWithSignal, systemClock, throwIfAborted, type RuntimeClock } from '../runtime/clock.js';
import { relayOrigin } from './endpoint.js';
import type { HttpRelayTransport } from './http.js';

export type AuthenticationIdentity = { readonly mode: 'account'; readonly signer: AccountSigner } | { readonly mode: 'device'; readonly signer: DeviceSigner };
export type AuthenticationSender = (method: string, parameters: JsonObject, signal?: AbortSignal) => Promise<JsonValue | undefined>;

/** Keeps bearer credentials out of ordinary object enumeration and diagnostic serialization. */
export class RelaySession {
    readonly #credentials: SessionCredentials;
    readonly #clock: RuntimeClock;
    readonly #started: number;
    readonly #lifetimeMilliseconds: number;
    constructor(credentials: SessionCredentials, readonly origin: string, challengeCreatedAt: number, started: number, clock: RuntimeClock) {
        this.#credentials = sessionCredentialsCodec.decode(sessionCredentialsCodec.encode(credentials));
        this.#started = started;
        this.#clock = clock;
        this.#lifetimeMilliseconds = (credentials.expiresAt - challengeCreatedAt - 1) * 1000;
    }
    get mode(): SessionMode { return this.#credentials.mode; }
    get lifetimeSeconds(): number { return this.#lifetimeMilliseconds / 1000; }
    get remainingSeconds(): number { return (this.#lifetimeMilliseconds - (this.#clock.monotonicMilliseconds() - this.#started)) / 1000; }
    /** Return only for sending a request to origin. Do not log or persist. */
    getToken(endpoint: string): string {
        if (relayOrigin(endpoint) !== this.origin) throw new ProtocolError('invalid_origin', 'A session cannot be sent to a different origin.');
        if (this.remainingSeconds <= 0) throw new ProtocolError('expired_session', 'The relay session has expired.');
        return this.#credentials.token;
    }
}

/** Captures immutable identity at binding time; certificate renewal for the same device is allowed. */
export class RelayAuthenticator {
    readonly #identity: AuthenticationIdentity;
    readonly #publicKey: Uint8Array | undefined;
    readonly accountId: string;
    readonly deviceId: string | undefined;
    readonly mode: SessionMode;
    constructor(readonly relayId: string, readonly context: NetworkContext, identity: AuthenticationIdentity, readonly clock: RuntimeClock = systemClock) {
        validateRelayId(relayId);
        this.#identity = { ...identity };
        this.mode = identity.mode;
        if (identity.mode === 'account') {
            this.accountId = identity.signer.accountId;
            this.#publicKey = identity.signer.publicKey.slice();
            if (!matchesAccount(this.accountId, this.#publicKey)) throw new ProtocolError('invalid_identity', 'The signer public key does not identify its account.');
        } else {
            const certificate = identity.signer.certificate;
            validateCertificate(certificate, context);
            this.accountId = certificate.account;
            this.deviceId = certificateId(certificate, context);
        }
    }

    assertIdentity(): void {
        const identity = this.#identity;
        if (identity.mode === 'account') {
            if (identity.signer.accountId !== this.accountId || !equalBytes(identity.signer.publicKey, this.#publicKey!))
                throw new ProtocolError('identity_changed', 'The account signer identity has changed.');
        } else {
            const certificate = identity.signer.certificate;
            if (certificate.account !== this.accountId || certificateId(certificate, this.context) !== this.deviceId)
                throw new ProtocolError('identity_changed', 'The device signer identity has changed.');
        }
    }

    async authenticate(endpoint: string, send: AuthenticationSender, signal?: AbortSignal): Promise<RelaySession> {
        throwIfAborted(signal);
        this.assertIdentity();
        const origin = relayOrigin(endpoint);
        // Clone before awaits: a mutable application object must not change the signed proof in flight.
        const identity = this.#identity;
        const certificate = identity.mode === 'device' ? deviceCertificateCodec.decode(deviceCertificateCodec.encode(identity.signer.certificate)) : undefined;
        if (certificate) validateCertificate(certificate, this.context);
        const started = this.clock.monotonicMilliseconds();
        const challengeWire = await send('auth.challenge', { account: this.accountId }, signal);
        if (challengeWire === undefined) throw new ProtocolError('missing_result', 'Authentication challenge has no result.');
        const challenge = authenticationChallengeCodec.decode(challengeWire);
        validateChallenge(challenge);
        this.assertIdentity();
        let method: string;
        let proof: JsonObject;
        if (identity.mode === 'device') {
            const request: DeviceAuthenticationRequest = { nonce: challenge.nonce, timestamp: this.clock.nowSeconds(), signerCertificate: certificate!, deviceSignature: new Uint8Array(64) };
            const input = deviceAuthenticationInput(request, this.relayId, endpoint, this.context);
            const signature = await identity.signer.sign(input.slice(), signal);
            if (!verifyDevice(input, signature, certificate!.signingPublicKey)) throw new ProtocolError('invalid_signature', 'The device signer returned an invalid signature.');
            proof = deviceAuthenticationCodec.encode({ ...request, deviceSignature: signature });
            method = 'auth.device.verify';
        } else {
            const request: AccountAuthenticationRequest = { nonce: challenge.nonce, accountPublicKey: this.#publicKey!.slice(), accountSignature: new Uint8Array(64) };
            const input = accountAuthenticationInput(request, this.accountId, this.relayId, endpoint, this.context);
            const signature = await identity.signer.sign(input.slice(), signal);
            if (!verifyAccount(input, signature, this.#publicKey!)) throw new ProtocolError('invalid_signature', 'The account signer returned an invalid signature.');
            proof = accountAuthenticationCodec.encode({ ...request, accountSignature: signature });
            method = 'auth.account.verify';
        }
        throwIfAborted(signal);
        this.assertIdentity();
        if (this.clock.monotonicMilliseconds() - started >= (challenge.expiresAt - challenge.createdAt) * 1000)
            throw new ProtocolError('expired_challenge', 'The challenge expired before the proof could be submitted.');
        const sessionWire = await send(method, proof, signal);
        throwIfAborted(signal);
        this.assertIdentity();
        if (sessionWire === undefined) throw new ProtocolError('missing_result', 'Authentication verification has no result.');
        const credentials = sessionCredentialsCodec.decode(sessionWire);
        validateSession(credentials);
        const session = new RelaySession(credentials, origin, challenge.createdAt, started, this.clock);
        if (session.mode !== this.mode || session.remainingSeconds <= 0) throw new ProtocolError('invalid_session', 'Relay returned an expired session or a different authentication mode.');
        return session;
    }
}

/** Shares only authentication work. Canceling a waiter never cancels another caller's refresh. */
export class HttpRelaySessions {
    readonly #lifetime = createAbortController();
    #session: RelaySession | undefined;
    #pending: { origin: string; promise: Promise<RelaySession> } | undefined;
    constructor(readonly authenticator: RelayAuthenticator, readonly transport: HttpRelayTransport) {}

    get(endpoint: string, signal?: AbortSignal): Promise<RelaySession> {
        throwIfAborted(signal);
        throwIfAborted(this.#lifetime.signal);
        this.authenticator.assertIdentity();
        const origin = relayOrigin(endpoint);
        if (this.#session?.origin === origin && this.#session.remainingSeconds > Math.min(5, this.#session.lifetimeSeconds * 0.2)) return Promise.resolve(this.#session);
        if (this.#pending?.origin === origin) return awaitWithSignal(this.#pending.promise, signal);
        const scope = abortScope([this.#lifetime.signal]);
        const pending = { origin, promise: this.authenticator.authenticate(endpoint,
            (name, parameters, token) => this.transport.request(endpoint, 'POST', name, parameters, undefined, token), scope.signal) };
        this.#pending = pending;
        pending.promise.then(session => {
            scope.dispose();
            if (this.#pending === pending) { this.#session = session; this.#pending = undefined; }
        }, () => { scope.dispose(); if (this.#pending === pending) this.#pending = undefined; });
        return awaitWithSignal(pending.promise, signal);
    }

    invalidate(session?: RelaySession): void { if (session === undefined || this.#session === session) this.#session = undefined; }
    dispose(): void { this.#session = undefined; this.#pending = undefined; this.#lifetime.abort(new DOMException('Relay sessions were disposed.', 'AbortError')); }
}
