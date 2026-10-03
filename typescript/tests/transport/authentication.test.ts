import { expect, test } from 'vitest';
import {
    NetworkContext, RelayAuthenticator, HttpRelaySessions, HttpRelayTransport, accountAuthenticationCodec, accountAuthenticationInput,
    decodeBase64Url, decodeUtf8, deviceAuthenticationCodec, deviceAuthenticationInput, relayOrigin,
    signAccount, signDevice, validateChallenge, validateSession, verifyAccount, verifyDevice, webEndpoint,
    type AccountSigner, type JsonObject, type JsonValue, type AuthenticationIdentity,
} from '../../packages/sdk/src/index.js';
import { AdvancingClock } from '../support/clock.js';
import { hex, vector } from '../support/vectors.js';

interface Fixture {
    network_context: string;
    device_certificate: { private_key: string; device_private_key: string };
    relay_origin: {
        normalization_cases: { name: string; endpoint: string; expected_origin: string; expected_origin_utf8_hex: string }[];
        comparison_cases: { name: string; left_endpoint: string; right_endpoint: string; expected_same_origin: boolean }[];
    };
    session_auth: {
        target: { relay_id: string; endpoint: string }; challenge: JsonObject;
        account_auth: { request: JsonObject; signing_input_canonical_json: string; signing_input_utf8_hex: string; signature: string; legacy_signature: string };
        device_auth: { request: JsonObject; signing_input_canonical_json: string; signing_input_utf8_hex: string; signature: string; legacy_signature: string };
        verification_cases: { name: string; trusted_relay_id: string; endpoint: string; network_context: string; signature_field: 'signature' | 'legacy_signature'; expected_signature_valid: boolean; nonce_override?: string; request_extra_fields?: JsonObject }[];
    };
}
const fixture = vector<Fixture>('identity-auth');
const session = fixture.session_auth;
const context = NetworkContext.parse(fixture.network_context);
const certificate = deviceAuthenticationCodec.decode(session.device_auth.request).signerCertificate;
const accountSigner: AccountSigner = { accountId: certificate.account, publicKey: certificate.accountPublicKey,
    sign: async input => signAccount(input, decodeBase64Url(fixture.device_certificate.private_key)) };
const deviceSigner = { certificate, sign: async (input: Uint8Array) => signDevice(input, decodeBase64Url(fixture.device_certificate.device_private_key)) };

test.each(fixture.relay_origin.normalization_cases)('origin vector $name', row => {
    expect(relayOrigin(row.endpoint)).toBe(row.expected_origin);
    expect(hex(new TextEncoder().encode(relayOrigin(row.endpoint)))).toBe(row.expected_origin_utf8_hex);
});
test.each(fixture.relay_origin.comparison_cases)('origin comparison $name', row => {
    expect(relayOrigin(row.left_endpoint) === relayOrigin(row.right_endpoint)).toBe(row.expected_same_origin);
});
test.each(['http://relay.example', 'HTTPS://relay.example', 'https://u@relay.example', 'https://relay.example?', 'https://relay.example#',
    ' https://relay.example', 'https://relay.example\n', 'https://relay.example\\evil', 'https://relay.example/%zz', 'https://%72elay.example'])('rejects unsafe web endpoint %j', endpoint => {
    expect(() => webEndpoint(endpoint)).toThrow();
});
test('fixed account and device authentication signing bytes', () => {
    const account = accountAuthenticationInput(accountAuthenticationCodec.decode(session.account_auth.request), certificate.account, session.target.relay_id, session.target.endpoint, context);
    const device = deviceAuthenticationInput(deviceAuthenticationCodec.decode(session.device_auth.request), session.target.relay_id, session.target.endpoint, context);
    expect(hex(account)).toBe(session.account_auth.signing_input_utf8_hex);
    expect(hex(device)).toBe(session.device_auth.signing_input_utf8_hex);
    expect(decodeUtf8(account)).toBe(session.account_auth.signing_input_canonical_json);
    expect(decodeUtf8(device)).toBe(session.device_auth.signing_input_canonical_json);
});
test.each(session.verification_cases)('authentication target binding $name', row => {
    const accountRequest = accountAuthenticationCodec.decode({ ...session.account_auth.request, ...row.request_extra_fields,
        ...(row.nonce_override === undefined ? {} : { nonce: row.nonce_override }) });
    const deviceRequest = deviceAuthenticationCodec.decode({ ...session.device_auth.request, ...row.request_extra_fields,
        ...(row.nonce_override === undefined ? {} : { nonce: row.nonce_override }) });
    const ctx = NetworkContext.parse(row.network_context);
    expect(verifyAccount(accountAuthenticationInput(accountRequest, certificate.account, row.trusted_relay_id, row.endpoint, ctx),
        decodeBase64Url(session.account_auth[row.signature_field]), accountRequest.accountPublicKey)).toBe(row.expected_signature_valid);
    expect(verifyDevice(deviceAuthenticationInput(deviceRequest, row.trusted_relay_id, row.endpoint, ctx),
        decodeBase64Url(session.device_auth[row.signature_field]), certificate.signingPublicKey)).toBe(row.expected_signature_valid);
});

const credentials = (mode: 'account' | 'device', expiresAt = 1730003600): JsonObject => ({ token: 'test-bearer-token', mode, expires_at: expiresAt });

test.each(['account', 'device'] as const)('authenticates %s with relay time and monotonic lifetime despite local wall clock skew', async mode => {
    const clock = new AdvancingClock();
    clock.wall += 100000;
    const identity: AuthenticationIdentity = mode === 'account' ? { mode, signer: accountSigner } : { mode, signer: deviceSigner };
    const auth = new RelayAuthenticator(session.target.relay_id, context, identity, clock);
    const calls: { method: string; proof: JsonObject }[] = [];
    const established = await auth.authenticate(session.target.endpoint, async (method, proof) => {
        calls.push({ method, proof }); clock.elapsed += 500;
        return method === 'auth.challenge' ? session.challenge : credentials(mode);
    });
    expect(calls.map(call => call.method)).toEqual(['auth.challenge', `auth.${mode}.verify`]);
    expect(established.remainingSeconds).toBe(3598);
    expect(established.getToken(session.target.endpoint)).toBe('test-bearer-token');
    expect(JSON.stringify(established)).not.toContain('test-bearer-token');
    expect(() => established.getToken('https://other.example')).toThrow('origin');
    clock.wall -= 2000000;
    clock.elapsed += 3598000;
    expect(() => established.getToken(session.target.endpoint)).toThrow('expired');
    if (mode === 'device') expect(calls[1]!.proof.timestamp).toBe(1730100000);
});

test('slow signer cannot submit a proof after challenge expiration', async () => {
    const clock = new AdvancingClock();
    const auth = new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer: {
        ...accountSigner, sign: async input => { clock.elapsed += 300000; return accountSigner.sign(input); },
    } }, clock);
    let calls = 0;
    await expect(auth.authenticate(session.target.endpoint, async () => { calls++; return session.challenge; })).rejects.toThrow('expired');
    expect(calls).toBe(1);
});

test('changed signer identity or invalid signature never submits verification', async () => {
    for (const changeIdentity of [true, false]) {
        const signer = { ...accountSigner, sign: async (input: Uint8Array) => {
            if (changeIdentity) signer.accountId = 'changed';
            return changeIdentity ? accountSigner.sign(input) : new Uint8Array(64);
        } };
        const auth = new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer }, new AdvancingClock());
        let calls = 0;
        await expect(auth.authenticate(session.target.endpoint, async () => { calls++; return session.challenge; })).rejects.toThrow();
        expect(calls).toBe(1);
    }
});

test.each([{ token: 'x', mode: 'device', expires_at: 1730003600 }, { token: 'x', mode: 'account', expires_at: 1730000001 }, { token: 'bad token', mode: 'account', expires_at: 1730003600 }])('rejects invalid session %j', async response => {
    const auth = new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer: accountSigner }, new AdvancingClock());
    await expect(auth.authenticate(session.target.endpoint, async name => name === 'auth.challenge' ? session.challenge : response)).rejects.toThrow();
});

test.each(['', ' ', 'token\r\nX:bad', 'é', 'x'.repeat(257)])('rejects noncanonical challenge and session token %j', token => {
    expect(() => validateChallenge({ nonce: token, createdAt: 0, expiresAt: 1 })).toThrow();
    expect(() => validateSession({ token, mode: 'account', expiresAt: 1 })).toThrow();
});

test('concurrent HTTP callers share refresh; canceling a waiter leaves remaining callers intact', async () => {
    const clock = new AdvancingClock();
    let resolveChallenge!: (value: Response) => void;
    let challengeCount = 0;
    const http = new HttpRelayTransport({ fetch: async url => {
        if (url.endsWith('/auth/challenge')) { challengeCount++; return new Promise(resolve => { resolveChallenge = resolve; }); }
        return new Response(JSON.stringify(credentials('account')), { headers: { 'Content-Type': 'application/json' } });
    } });
    const sessions = new HttpRelaySessions(new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer: accountSigner }, clock), http);
    const controller = new AbortController();
    const canceled = sessions.get(session.target.endpoint, controller.signal);
    const remaining = sessions.get(session.target.endpoint);
    const assertion = expect(canceled).rejects.toThrow('canceled waiter');
    controller.abort(new Error('canceled waiter'));
    await assertion;
    resolveChallenge(new Response(JSON.stringify(session.challenge), { headers: { 'Content-Type': 'application/json' } }));
    const established = await remaining;
    expect(challengeCount).toBe(1);
    expect(await sessions.get('https://RELAY.example:443/different/path')).toBe(established);
    sessions.invalidate(established);
    const refresh = sessions.get(session.target.endpoint);
    await Promise.resolve();
    resolveChallenge(new Response(JSON.stringify(session.challenge), { headers: { 'Content-Type': 'application/json' } }));
    await refresh;
    expect(challengeCount).toBe(2);
    sessions.dispose(); http.dispose();
    expect(() => sessions.get(session.target.endpoint)).toThrow();
});

test('failed refresh is visible and a later call may attempt authentication again', async () => {
    let attempts = 0;
    const http = new HttpRelayTransport({ fetch: async url => {
        if (url.endsWith('/auth/challenge')) {
            if (++attempts === 1) throw new Error('connection dropped');
            return new Response(JSON.stringify(session.challenge), { headers: { 'Content-Type': 'application/json' } });
        }
        return new Response(JSON.stringify(credentials('account')), { headers: { 'Content-Type': 'application/json' } });
    } });
    const sessions = new HttpRelaySessions(new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer: accountSigner }, new AdvancingClock()), http);
    await expect(sessions.get(session.target.endpoint)).rejects.toThrow('connection dropped');
    expect((await sessions.get(session.target.endpoint)).mode).toBe('account');
    expect(attempts).toBe(2);
    sessions.dispose(); http.dispose();
});

test.each([6, 61])('HTTP sessions refresh before expiry for a %i-second credential lifetime', async lifetime => {
    const clock = new AdvancingClock(); let proofs = 0;
    const http = new HttpRelayTransport({ fetch: async url => new Response(JSON.stringify(url.endsWith('/auth/challenge')
        ? { nonce: 'renewal-challenge', created_at: clock.wall, expires_at: clock.wall + 300 }
        : { token: `renewed-${++proofs}`, mode: 'account', expires_at: clock.wall + lifetime }), { headers: { 'Content-Type': 'application/json' } }) });
    const sessions = new HttpRelaySessions(new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer: accountSigner }, clock), http);
    try {
        const original = await sessions.get(session.target.endpoint); const margin = lifetime === 6 ? 1 : 5;
        clock.elapsed = (lifetime - 1 - margin) * 1000 - 1;
        expect(await sessions.get(session.target.endpoint)).toBe(original); expect(proofs).toBe(1);
        clock.elapsed++; expect(original.remainingSeconds).toBe(margin);
        const [first, second] = await Promise.all([sessions.get(session.target.endpoint), sessions.get(session.target.endpoint)]);
        expect(first).not.toBe(original); expect(second).toBe(first); expect(proofs).toBe(2);
        expect(first.getToken(session.target.endpoint)).toBe('renewed-2');
    } finally { sessions.dispose(); http.dispose(); }
});

test('a failed near-expiry refresh is returned without falling back to the old HTTP token', async () => {
    const clock = new AdvancingClock(); let attempts = 0;
    const http = new HttpRelayTransport({ fetch: async url => {
        if (url.endsWith('/auth/challenge')) {
            if (++attempts === 2) throw new Error('refresh unavailable');
            return new Response(JSON.stringify({ nonce: 'renewal-challenge', created_at: clock.wall, expires_at: clock.wall + 300 }), { headers: { 'Content-Type': 'application/json' } });
        }
        return new Response(JSON.stringify({ token: `credential-${attempts}`, mode: 'account', expires_at: clock.wall + 61 }), { headers: { 'Content-Type': 'application/json' } });
    } });
    const sessions = new HttpRelaySessions(new RelayAuthenticator(session.target.relay_id, context, { mode: 'account', signer: accountSigner }, clock), http);
    try {
        const original = await sessions.get(session.target.endpoint); clock.elapsed = 56000; expect(original.remainingSeconds).toBe(4);
        await expect(sessions.get(session.target.endpoint)).rejects.toThrow('refresh unavailable');
        expect((await sessions.get(session.target.endpoint)).getToken(session.target.endpoint)).toBe('credential-3');
    } finally { sessions.dispose(); http.dispose(); }
});
