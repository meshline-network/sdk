import { readFileSync } from 'node:fs';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { URL as ExpoUrl } from 'whatwg-url-minimum';
import { relayOrigin, webEndpoint } from '@meshline/sdk';
import { parseAbsoluteUrl } from '../../packages/sdk/dist/runtime/url.js';

const vectors = JSON.parse(readFileSync(new URL('../../../tests/vectors/identity-auth-v1.json', import.meta.url), 'utf8')) as {
    relay_origin: { normalization_cases: { name: string; endpoint: string; expected_origin: string }[];
        comparison_cases: { name: string; left_endpoint: string; right_endpoint: string; expected_same_origin: boolean }[] };
};
// This is Expo 57's actual URL implementation running on Node, not a Hermes claim.
beforeEach(() => { vi.stubGlobal('URL', ExpoUrl); });
afterEach(() => { vi.unstubAllGlobals(); });
test.each(vectors.relay_origin.normalization_cases)('Expo URL with portable IDNA: $name', row => {
    expect(relayOrigin(row.endpoint)).toBe(row.expected_origin);
});
test.each(vectors.relay_origin.comparison_cases)('Expo origin comparison: $name', row => {
    expect(relayOrigin(row.left_endpoint) === relayOrigin(row.right_endpoint)).toBe(row.expected_same_origin);
});
test.each(['https://ab\u200dcd.example', 'https://abcא.example', 'https://xn--a.example', 'https://bad%2fhost.example', 'https://bad%40host.example'])('invalid or escaped IDNA authority cannot change parsed host: %s', value => {
    expect(() => parseAbsoluteUrl(value)).toThrow();
});
test('content URLs retain credentials, path, query and fragment while normalizing only the host', () => {
    const parsed = parseAbsoluteUrl('https://user:pass@faß.example:8443/中文?q=😀#part');
    expect(parsed.hostname).toBe('xn--fa-hia.example'); expect(parsed.username).toBe('user'); expect(parsed.password).toBe('pass');
    expect(parsed.port).toBe('8443'); expect(parsed.pathname).toBe('/%E4%B8%AD%E6%96%87'); expect(parsed.hash).toBe('#part');
});
test('relay validation still rejects escaped authorities before portable normalization', () => {
    expect(() => webEndpoint('https://%72elay.example')).toThrow(); expect(() => webEndpoint('https://user@例子.测试')).toThrow();
});
