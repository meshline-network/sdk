import { ProtocolError } from '../errors.js';

const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

/** Rejects unpaired UTF-16 surrogates without normalizing valid text. */
export function validateUnicode(value: string): void {
    for (let index = 0; index < value.length; index++) {
        const unit = value.charCodeAt(index);
        if (unit >= 0xd800 && unit <= 0xdbff) {
            const next = value.charCodeAt(++index);
            if (!(next >= 0xdc00 && next <= 0xdfff))
                throw new ProtocolError('invalid_unicode', 'Unpaired high surrogate.');
        } else if (unit >= 0xdc00 && unit <= 0xdfff) {
            throw new ProtocolError('invalid_unicode', 'Unpaired low surrogate.');
        }
    }
}

/** Encodes Unicode scalar values as UTF-8 without silently replacing invalid text. */
export function encodeUtf8(value: string): Uint8Array {
    validateUnicode(value);
    return new TextEncoder().encode(value);
}

/** Strict UTF-8 decoding, independent of platform TextDecoder error handling. */
export function decodeUtf8(bytes: Uint8Array): string {
    const chunks: string[] = [];
    let chunk = '';
    for (let index = 0; index < bytes.length;) {
        const first = bytes[index++]!;
        let scalar: number;
        let count: number;
        let minimum: number;
        if (first <= 0x7f) { scalar = first; count = 0; minimum = 0; }
        else if (first >= 0xc2 && first <= 0xdf) { scalar = first & 31; count = 1; minimum = 0x80; }
        else if (first >= 0xe0 && first <= 0xef) { scalar = first & 15; count = 2; minimum = 0x800; }
        else if (first >= 0xf0 && first <= 0xf4) { scalar = first & 7; count = 3; minimum = 0x10000; }
        else throw new ProtocolError('invalid_utf8', 'Invalid UTF-8 leading byte.');
        for (let part = 0; part < count; part++) {
            const continuation = bytes[index++];
            if (continuation === undefined || (continuation & 0xc0) !== 0x80)
                throw new ProtocolError('invalid_utf8', 'Invalid or incomplete UTF-8 sequence.');
            scalar = (scalar << 6) | (continuation & 63);
        }
        if (scalar < minimum || scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff))
            throw new ProtocolError('invalid_utf8', 'Noncanonical UTF-8 scalar.');
        chunk += String.fromCodePoint(scalar);
        if (chunk.length >= 4096) { chunks.push(chunk); chunk = ''; }
    }
    chunks.push(chunk);
    return chunks.join('');
}

/** Encodes bytes as canonical unpadded base64url. */
export function encodeBase64Url(bytes: Uint8Array): string {
    const chunks: string[] = [];
    let chunk = '';
    for (let index = 0; index < bytes.length; index += 3) {
        const first = bytes[index]!;
        const second = bytes[index + 1];
        const third = bytes[index + 2];
        chunk += alphabet[first >>> 2]! + alphabet[((first & 3) << 4) | ((second ?? 0) >>> 4)]!;
        if (second !== undefined) chunk += alphabet[((second & 15) << 2) | ((third ?? 0) >>> 6)]!;
        if (third !== undefined) chunk += alphabet[third & 63]!;
        if (chunk.length >= 4096) { chunks.push(chunk); chunk = ''; }
    }
    chunks.push(chunk);
    return chunks.join('');
}

/** Decodes only canonical unpadded base64url, including zero trailing pad bits. */
export function decodeBase64Url(value: string, expectedLength?: number): Uint8Array {
    if (/[^A-Za-z0-9_-]/.test(value) || value.length % 4 === 1)
        throw new ProtocolError('invalid_base64url', 'Expected canonical unpadded base64url.');
    const tail = value.length === 0 ? 0 : alphabet.indexOf(value.at(-1)!);
    if ((value.length % 4 === 2 && (tail & 15) !== 0) || (value.length % 4 === 3 && (tail & 3) !== 0))
        throw new ProtocolError('invalid_base64url', 'Nonzero base64url padding bits.');
    const result = new Uint8Array(Math.floor(value.length * 6 / 8));
    if (expectedLength !== undefined && result.length !== expectedLength)
        throw new ProtocolError('invalid_length', `Expected ${expectedLength} bytes.`);
    let bits = 0;
    let available = 0;
    let offset = 0;
    for (const character of value) {
        bits = (bits << 6) | alphabet.indexOf(character);
        available += 6;
        if (available >= 8) { available -= 8; result[offset++] = (bits >>> available) & 255; }
    }
    return result;
}

/** Concatenates byte arrays without retaining references to the inputs. */
export function concatBytes(...values: readonly Uint8Array[]): Uint8Array {
    const result = new Uint8Array(values.reduce((size, value) => size + value.length, 0));
    let offset = 0;
    for (const value of values) { result.set(value, offset); offset += value.length; }
    return result;
}

/** Compares byte values without an early exit on unequal content. */
export function equalBytes(left: Uint8Array, right: Uint8Array): boolean {
    if (left.length !== right.length) return false;
    let difference = 0;
    for (let index = 0; index < left.length; index++) difference |= left[index]! ^ right[index]!;
    return difference === 0;
}
