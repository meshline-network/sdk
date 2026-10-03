import { expect, test } from 'vitest';
import {
    HttpRelayTransport, RelayBackoff, RelayError, validateJsonContentType, validateRetryAfterHeader,
    type RelayFetch, type RelayFetchInit, type JsonObject,
} from '../../packages/sdk/src/index.js';
import { AdvancingClock } from '../support/clock.js';

const endpoint = 'https://RELAY.example:443/a/meshline/v1/';
function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
}
function fixed(response: () => Response): HttpRelayTransport { return new HttpRelayTransport({ fetch: async () => response() }); }

test('GET preserves scalar names and escaping and never sends a body, credentials or referrer', async () => {
    let captured: { url: string; init: RelayFetchInit } | undefined;
    const http = new HttpRelayTransport({ fetch: async (url, init) => { captured = { url, init }; return json({ ok: true }); } });
    expect(await http.request(endpoint, 'GET', 'account.route.resolve', { account: 'a/b?&😀', enabled: true, before_sequence: 0, 'future!': "'()" }, 'test-token')).toEqual({ ok: true });
    expect(captured!.url).toBe('https://relay.example/a/meshline/v1/account/route/resolve?account=a%2Fb%3F%26%F0%9F%98%80&enabled=true&before_sequence=0&future%21=%27%28%29');
    expect(captured!.init).toMatchObject({ method: 'GET', redirect: 'error', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer', headers: { Accept: 'application/json', 'X-Meshline-Session': 'test-token' } });
    expect(captured!.init.body).toBeUndefined();
});

test('POST encodes canonical protocol JSON and cannot inject token headers', async () => {
    let body: string | undefined;
    const http = new HttpRelayTransport({ fetch: async (_, init) => { body = init.body; expect(init.headers['Content-Type']).toBe('application/json; charset=utf-8'); return new Response(null, { status: 204 }); } });
    expect(await http.request(endpoint, 'POST', 'message.send', { z: 2, a: '中文' })).toBeUndefined();
    expect(body).toBe('{"a":"中文","z":2}');
    await expect(http.request(endpoint, 'POST', 'message.send', {}, 'foo\r\nOther:bad')).rejects.toThrow();
});

test.each([null, {}, [], 1.5])('rejects nonscalar or invalid GET values %j before sending', async value => {
    let calls = 0;
    const http = new HttpRelayTransport({ fetch: async () => { calls++; return json({}); } });
    await expect(http.request(endpoint, 'GET', 'channel.read', { limit: value })).rejects.toThrow();
    expect(calls).toBe(0);
});
test.each(['../message.send', '/x', 'message..send', 'message/send', 'auth.challenge\n'])('rejects invalid method %j', async name => {
    await expect(fixed(() => json({})).request(endpoint, 'POST', name)).rejects.toThrow();
});

test.each(['Application/JSON; Charset="UTF-8"', 'application/json', 'application/json; vendor="hello;world"; charset=utf-8', 'application/json ;charset=utf-8; charset=UTF-8'])('accepts UTF8 JSON media type %s', value => {
    expect(() => validateJsonContentType(value)).not.toThrow();
});
test.each([null, 'text/json', 'application/jsonx', 'application/json; charset=utf8', 'application/json; charset=utf-8; charset=latin1', 'application/json, text/html', 'application/json; charset', 'application/json; broken="unterminated'])('rejects unsafe content type %j', value => {
    expect(() => validateJsonContentType(value)).toThrow();
});
test.each([201, 202, 206])('result must be HTTP200, not %s', async status => {
    await expect(fixed(() => json({}, status)).request(endpoint, 'GET', 'relay.info')).rejects.toThrow('HTTP 200');
});
test('rejects redirects, duplicate members, noncanonical numbers and invalid UTF8', async () => {
    const responses = [
        () => new Response(null, { status: 302, headers: { location: 'https://other.example' } }),
        () => new Response('{"a":1,"a":2}', { headers: { 'content-type': 'application/json' } }),
        () => new Response('{"a":1e0}', { headers: { 'content-type': 'application/json' } }),
        () => new Response(new Uint8Array([123, 34, 97, 34, 58, 34, 0xff, 34, 125]), { headers: { 'content-type': 'application/json' } }),
    ];
    for (const response of responses) await expect(fixed(response).request(endpoint, 'GET', 'relay.info')).rejects.toThrow();
});

test('preserves unknown structured errors and distinguishes rejection from uncertain outcome', async () => {
    for (const [code, definitive] of [['forbidden', true], ['internal_error', false], ['future_error', false]] as const) {
        try { await fixed(() => json({ code, message: 'failed', data: { future: [1, null] }, extension: true }, 500)).request(endpoint, 'POST', 'message.send'); throw new Error('Expected relay error'); }
        catch (error) {
            expect(error).toBeInstanceOf(RelayError);
            const failure = error as RelayError;
            expect(failure.code).toBe(code);
            expect(failure.isDefinitiveRejection).toBe(definitive);
            expect(failure.failure.data).toEqual({ future: [1, null] });
            expect(failure.failure.additionalProperties).toEqual({ extension: true });
        }
    }
});

test('rate limit rejects current request once and delays the next request using monotonic time', async () => {
    const clock = new AdvancingClock();
    const backoff = new RelayBackoff(clock);
    let calls = 0;
    const http = new HttpRelayTransport({ backoff, fetch: async () => ++calls === 1
        ? json({ code: 'rate_limited', message: 'busy', data: { retry_after: 125 } }, 429, { 'retry-after': '125' }) : json({ ok: true }) });
    await expect(http.request(endpoint, 'POST', 'message.send')).rejects.toMatchObject({ code: 'rate_limited', retryAfter: 125 });
    expect(calls).toBe(1);
    expect(clock.delays).toEqual([]);
    clock.wall -= 100000;
    await http.request(endpoint, 'GET', 'relay.info');
    expect(calls).toBe(2);
    expect(clock.delays).toEqual([60000, 60000, 5000]);
});

test.each([
    [429, { retry_after: 3 }, null], [400, { retry_after: 3 }, '4'], [429, { retry_after: 3 }, '3, 3'],
    [429, { retry_after: 3 }, 'Wed, 21 Oct 2015 07:28:00 GMT'], [429, {}, '3'], [429, { retry_after: -1 }, '-1'],
    [429, { retry_after: 3 }, '3\n'],
] satisfies [number, JsonObject, string | null][])('rejects mismatched rate-limit evidence %s %j %j', (status, data, header) => {
    expect(() => validateRetryAfterHeader({ code: 'rate_limited', message: 'busy', data }, status, header)).toThrow();
});

test('missing retry hint uses one second and a shorter hint cannot reduce existing backoff', async () => {
    const clock = new AdvancingClock();
    const backoff = new RelayBackoff(clock);
    backoff.apply({ code: 'rate_limited', message: 'busy' }); await backoff.wait();
    expect(clock.elapsed).toBe(1000);
    backoff.apply({ code: 'rate_limited', message: 'busy', data: { retry_after: 10 } });
    backoff.apply({ code: 'rate_limited', message: 'busy', data: { retry_after: 2 } });
    await backoff.wait();
    expect(clock.elapsed).toBe(11000);
});

test('cancellation and request timeout abort transport work without retry', async () => {
    let calls = 0;
    const pending: RelayFetch = async (_, init) => { calls++; return new Promise((_, reject) => {
        init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true });
    }); };
    const http = new HttpRelayTransport({ fetch: pending, requestTimeoutMilliseconds: 20 });
    await expect(http.request(endpoint, 'GET', 'relay.info')).rejects.toMatchObject({ name: 'TimeoutError' });
    const controller = new AbortController();
    const request = http.request(endpoint, 'GET', 'relay.info', undefined, undefined, controller.signal);
    const assertion = expect(request).rejects.toThrow('canceled');
    await Promise.resolve();
    controller.abort(new Error('canceled'));
    await assertion;
    expect(calls).toBe(2);
    http.dispose();
    await expect(http.request(endpoint, 'GET', 'relay.info')).rejects.toThrow('disposed');
    expect(calls).toBe(2);
});
