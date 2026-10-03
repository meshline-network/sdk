import { ProtocolError } from '../errors.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { parseAbsoluteUrl } from '../runtime/url.js';

/** Validates before URL parsing so a parser cannot silently repair unsafe input. */
export function webEndpoint(endpoint: string, protocol?: 'https:' | 'wss:'): URL {
    if (typeof endpoint !== 'string' || !/^(https|wss):\/\//.test(endpoint) || encodeUtf8(endpoint).length > 512
        || /[\s\p{Cc}?#\\]/u.test(endpoint))
        throw new ProtocolError('invalid_endpoint', 'Expected an HTTPS or WSS relay endpoint without whitespace, query, fragment or backslash.');
    const authority = endpoint.slice(endpoint.indexOf('://') + 3).split('/')[0]!;
    if (authority.length === 0 || authority.includes('@') || authority.includes('%'))
        throw new ProtocolError('invalid_endpoint', 'Relay endpoints must have a host and cannot contain user information or an escaped authority.');
    // URL accepts malformed percent escapes in paths; the protocol does not.
    if (/%(?![0-9a-fA-F]{2})/.test(endpoint)) throw new ProtocolError('invalid_endpoint', 'Malformed endpoint escape.');
    let url: URL;
    try { url = parseAbsoluteUrl(endpoint); }
    catch (cause) { throw new ProtocolError('invalid_endpoint', 'Invalid relay endpoint.', { cause }); }
    if (!url.hostname || (protocol !== undefined && url.protocol !== protocol))
        throw new ProtocolError('invalid_endpoint', 'Unexpected relay endpoint protocol or host.');
    return url;
}

/** Authentication maps WSS to HTTPS and excludes the endpoint path. */
export function relayOrigin(endpoint: string): string {
    const url = webEndpoint(endpoint);
    return `https://${url.hostname.toLowerCase()}${url.port === '' || url.port === '443' ? '' : `:${url.port}`}`;
}

export function validateMethodName(name: string): void {
    if (!/^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)*$/.test(name) || /\s/.test(name))
        throw new ProtocolError('invalid_method', 'Invalid relay method name.');
}
