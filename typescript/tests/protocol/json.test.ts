import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import {
    NetworkContext, ProtocolError, canonicalBytes, canonicalJson, decodeBase64Url,
    decodeUtf8, encodeBase64Url, encodeUtf8, parseJson, requireObject,
    signingInput, validateIdentifier, type IdentifierKind, type JsonValue,
} from '../../packages/sdk/src/index.js';
import { hex, unhex, vector, vectorDirectory } from '../support/vectors.js';

interface CommonVectors {
    network_context: string;
    canonical_json: {
        vectors: Array<{ id: string; operation: string; input_json: string; input_utf8_hex: string; expected: { canonical_json: string; utf8_hex: string; sha256_hex: string } }>;
        rejection_vectors: Array<{ id: string; input_json: string }>;
    };
    base64url: { cases: Array<{ id?: string; name?: string; input: string; prefix?: string; expected_accepted: boolean; decoded_hex?: string }> };
    network_binding: { format_cases: Array<{ name?: string; input: unknown; expected_accepted: boolean }> };
}

const common = vector<CommonVectors>('common');
const context = NetworkContext.parse(common.network_context);

test('all fixture snapshots match recorded SHA-256 values', () => {
    const manifest = JSON.parse(readFileSync(new URL('manifest.json', vectorDirectory), 'utf8')) as { files: Record<string, string> };
    expect(Object.keys(manifest.files)).toHaveLength(7);
    for (const [name, expected] of Object.entries(manifest.files))
        expect(createHash('sha256').update(readFileSync(new URL(name, vectorDirectory))).digest('hex'), name).toBe(expected);
});

describe('independent canonical JSON vectors', () => {
    test.each(common.canonical_json.vectors)('$id', row => {
        expect(hex(encodeUtf8(row.input_json))).toBe(row.input_utf8_hex);
        const parsed = parseJson(row.input_json);
        const bytes = row.operation === 'network_bound_json' ? signingInput(requireObject(parsed), context) : canonicalBytes(parsed);
        expect(decodeUtf8(bytes)).toBe(row.expected.canonical_json);
        expect(hex(bytes)).toBe(row.expected.utf8_hex);
        expect(createHash('sha256').update(bytes).digest('hex')).toBe(row.expected.sha256_hex);
    });

    test.each(common.canonical_json.rejection_vectors)('reject $id', row => {
        expect(() => canonicalJson(parseJson(row.input_json))).toThrow(ProtocolError);
    });
});

describe('canonical base64url', () => {
    test.each(common.base64url.cases)('$input accepted=$expected_accepted', row => {
        const decode = (): Uint8Array => {
            if (!row.prefix) return decodeBase64Url(row.input);
            if (!row.input.startsWith(row.prefix)) throw new ProtocolError('invalid_prefix', 'Wrong prefix.');
            const kinds: Record<string, IdentifierKind> = { dev_: 'device', msg_: 'message', chan_: 'channel', grp_: 'group', inv_: 'invite' };
            const kind = kinds[row.prefix];
            if (kind) validateIdentifier(kind, row.input);
            return decodeBase64Url(row.input.slice(row.prefix.length), row.prefix === 'sha256:' ? 32 : 16);
        };
        if (!row.expected_accepted) expect(decode).toThrow(ProtocolError);
        else {
            const decoded = decode();
            expect(hex(decoded)).toBe(row.decoded_hex);
            expect((row.prefix ?? '') + encodeBase64Url(decoded)).toBe(row.input);
        }
    });

    test.each(['AAA\n', 'AAAA\r\n', 'AA=', 'AB', 'AAB', 'A', 'AA '])('rejects noncanonical %j', input => {
        expect(() => decodeBase64Url(input)).toThrow();
    });
});

test.each(common.network_binding.format_cases)('network context $input accepted=$expected_accepted', row => {
    const parse = (): NetworkContext => {
        if (typeof row.input !== 'string') throw new ProtocolError('invalid_context', 'Expected string.');
        return NetworkContext.parse(row.input);
    };
    if (row.expected_accepted) expect(parse().toString()).toBe(row.input);
    else expect(parse).toThrow(ProtocolError);
});

test.each(['c0af', 'e080af', 'f0808080', 'eda080', 'f4908080', '80', 'e282', 'ff'])('rejects invalid UTF-8 %s', input => {
    expect(() => decodeUtf8(unhex(input))).toThrow(ProtocolError);
});

test.each(['', 'null', '"é😀"', '{"__proto__":{},"constructor":1}', '[true,false,null]'])('strict parse %j', input => {
    if (input === '') expect(() => parseJson(input)).toThrow();
    else expect(canonicalJson(parseJson(input))).toBe(canonicalJson(JSON.parse(input) as JsonValue));
});

test('prototype-looking names survive without prototype pollution', () => {
    const result = requireObject(parseJson('{"__proto__":{"polluted":true}}'));
    expect(Object.getPrototypeOf(result)).toBeNull();
    expect(Object.hasOwn(result, '__proto__')).toBe(true);
    expect(canonicalJson(result)).toBe('{"__proto__":{"polluted":true}}');
});

test.each(['{"a":1,"\\u0061":2}', '{"x":{"a":1,"a":2}}', 'true false', '[1,]', '{"a":1,}', '\ufeff{}', '01', '1e0', '1.0', '-0'])('rejects ambiguous input %j', input => {
    expect(() => parseJson(input)).toThrow(ProtocolError);
});

test('signature field exclusions only affect the root object', () => {
    const document = requireObject(parseJson('{"signature":"omit","child":{"signature":"keep"},"extra":null}'));
    const bytes = signingInput(document, context, ['signature']);
    expect(parseJson(bytes)).toEqual({ $context: context.toString(), child: { signature: 'keep' }, extra: null });
    expect(document.signature).toBe('omit');
    expect(() => signingInput({ $context: context.toString() }, context)).toThrow();
});

test('depth sixteen is accepted and seventeen rejected', () => {
    const document = (depth: number): string => '['.repeat(depth) + '0' + ']'.repeat(depth);
    expect(canonicalJson(parseJson(document(16)))).toBe(document(16));
    expect(() => parseJson(document(17))).toThrow();
});

test('canonicalization rejects non-JSON application values', () => {
    for (const value of [undefined, NaN, Infinity, -0, 1.1, 9007199254740992, new Date(), new Uint8Array(1), Array(1), { value: undefined }])
        expect(() => canonicalJson(value as JsonValue)).toThrow(ProtocolError);
    expect(() => canonicalJson({ get x(): number { throw new Error('must not invoke getter'); } })).toThrow('accessors');
});
