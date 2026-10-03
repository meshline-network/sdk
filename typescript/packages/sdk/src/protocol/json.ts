import { ProtocolError } from '../errors.js';
import { decodeUtf8, encodeUtf8, validateUnicode } from './encoding.js';

export type JsonValue = null | boolean | number | string | JsonValue[] | JsonObject;
export interface JsonObject { [name: string]: JsonValue }
export const maximumJsonDepth = 16;

/** Checks the exact safe-integer domain used by protocol JSON. */
export function requireSafeInteger(value: unknown, minimum = -Number.MAX_SAFE_INTEGER): asserts value is number {
    if (typeof value !== 'number' || !Number.isSafeInteger(value) || Object.is(value, -0) || value < minimum)
        throw new ProtocolError('invalid_integer', 'Expected a safe integer in the allowed range.');
}

/** Parses protocol JSON before duplicate names or numeric spelling can be lost. */
export function parseJson(input: string | Uint8Array): JsonValue {
    const source = typeof input === 'string' ? input : decodeUtf8(input);
    validateUnicode(source);
    let position = 0;
    const fail = (message: string): never => { throw new ProtocolError('invalid_json', `${message} At offset ${position}.`); };
    const whitespace = (): void => { while (/[\x20\t\r\n]/.test(source[position] ?? '') && position < source.length) position++; };

    function string(): string {
        const start = position++;
        while (position < source.length) {
            const character = source[position++]!;
            if (character === '"') {
                let result: string;
                try { result = JSON.parse(source.slice(start, position)) as string; }
                catch (cause) { throw new ProtocolError('invalid_json', 'Invalid JSON string.', { cause }); }
                validateUnicode(result);
                return result;
            }
            if (character === '\\') {
                const escape = source[position++];
                if (escape === 'u') {
                    if (!/^[0-9a-fA-F]{4}$/.test(source.slice(position, position + 4))) fail('Invalid Unicode escape.');
                    position += 4;
                } else if (escape === undefined || !'"\\/bfnrt'.includes(escape)) fail('Invalid string escape.');
            } else if (character.charCodeAt(0) < 32) fail('Unescaped control character.');
        }
        return fail('Unterminated string.');
    }

    function value(depth: number): JsonValue {
        if (depth > maximumJsonDepth) fail('JSON depth exceeds 16.');
        whitespace();
        const character = source[position];
        if (character === '"') return string();
        if (character === '{') {
            position++;
            const result = Object.create(null) as JsonObject;
            whitespace();
            if (source[position] === '}') { position++; return result; }
            while (true) {
                whitespace();
                if (source[position] !== '"') fail('Expected an object property name.');
                const name = string();
                if (Object.hasOwn(result, name)) fail('Duplicate property name.');
                whitespace();
                if (source[position++] !== ':') fail('Expected a colon.');
                result[name] = value(depth + 1);
                whitespace();
                const separator = source[position++];
                if (separator === '}') return result;
                if (separator !== ',') fail('Expected an object separator.');
            }
        }
        if (character === '[') {
            position++;
            const result: JsonValue[] = [];
            whitespace();
            if (source[position] === ']') { position++; return result; }
            while (true) {
                result.push(value(depth + 1));
                whitespace();
                const separator = source[position++];
                if (separator === ']') return result;
                if (separator !== ',') fail('Expected an array separator.');
            }
        }
        for (const [literal, result] of [['true', true], ['false', false], ['null', null]] as const) {
            if (source.startsWith(literal, position)) { position += literal.length; return result; }
        }
        const start = position;
        while (position < source.length && /[0-9eE+.\-]/.test(source[position]!)) position++;
        const token = source.slice(start, position);
        if (!/^-?(0|[1-9][0-9]*)$/.test(token) || token === '-0') fail('Expected a shortest decimal integer.');
        const result = Number(token);
        requireSafeInteger(result);
        return result;
    }

    const result = value(0);
    whitespace();
    if (position !== source.length) fail('Trailing non-whitespace data.');
    return result;
}

/** Encodes only JSON data, preserving unknown properties and UTF-16 key ordering. */
export function canonicalJson(value: JsonValue): string {
    function encode(input: JsonValue, depth: number): string {
        if (depth > maximumJsonDepth) throw new ProtocolError('invalid_json', 'JSON depth exceeds 16.');
        if (input === null) return 'null';
        if (typeof input === 'string') { validateUnicode(input); return JSON.stringify(input); }
        if (typeof input === 'boolean') return input ? 'true' : 'false';
        if (typeof input === 'number') { requireSafeInteger(input); return String(input); }
        if (Array.isArray(input)) {
            const parts: string[] = [];
            for (let index = 0; index < input.length; index++) {
                if (!Object.hasOwn(input, index)) throw new ProtocolError('invalid_json', 'Sparse arrays are not JSON data.');
                parts.push(encode(input[index]!, depth + 1));
            }
            return `[${parts.join(',')}]`;
        }
        if (typeof input !== 'object' || ![Object.prototype, null].includes(Object.getPrototypeOf(input) as object | null))
            throw new ProtocolError('invalid_json', 'Expected plain JSON data.');
        if (Object.getOwnPropertySymbols(input).length !== 0)
            throw new ProtocolError('invalid_json', 'Symbol properties are not JSON data.');
        const properties = Object.keys(input).sort().map(name => {
            validateUnicode(name);
            const descriptor = Object.getOwnPropertyDescriptor(input, name)!;
            if (!('value' in descriptor)) throw new ProtocolError('invalid_json', 'JSON properties cannot be accessors.');
            return `${JSON.stringify(name)}:${encode(descriptor.value as JsonValue, depth + 1)}`;
        });
        return `{${properties.join(',')}}`;
    }
    return encode(value, 0);
}

/** Encodes canonical protocol JSON as strict UTF-8. */
export function canonicalBytes(value: JsonValue): Uint8Array {
    return encodeUtf8(canonicalJson(value));
}

/** Requires a JSON object; arrays and null do not qualify. */
export function requireObject(value: JsonValue): JsonObject {
    if (value === null || typeof value !== 'object' || Array.isArray(value))
        throw new ProtocolError('invalid_object', 'Expected a JSON object.');
    return value;
}
