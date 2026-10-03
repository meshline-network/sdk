import { ProtocolError } from '../errors.js';
import { decodeBase64Url } from '../protocol/encoding.js';

export function object(value: unknown): Record<string, unknown> {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ProtocolError('invalid_neo_data', 'Expected a JSON object.');
    return value as Record<string, unknown>;
}

export function text(value: unknown): string {
    if (typeof value !== 'string') throw new ProtocolError('invalid_neo_data', 'Expected a JSON string.');
    return value;
}

export function array(value: unknown): unknown[] {
    if (!Array.isArray(value)) throw new ProtocolError('invalid_neo_data', 'Expected a JSON array.');
    return value;
}

export function base64(value: unknown): Uint8Array {
    const encoded = text(value);
    if (encoded.length % 4 !== 0 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(encoded))
        throw new ProtocolError('invalid_neo_data', 'Expected padded Base64.');
    return decodeBase64Url(encoded.replace(/=+$/, '').replaceAll('+', '-').replaceAll('/', '_'));
}
