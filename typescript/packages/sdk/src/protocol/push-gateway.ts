import { ProtocolError } from '../errors.js';
import { encodeUtf8 } from './encoding.js';

// Use a platform-independent syntax check for signed callback addresses. DNS/UNC,
// IPv6 and IRI rules follow the C# SDK's System.Uri boundary; validation copies
// never replace the original address. Reference: dotnet/runtime System.Private.Uri.
function validHost(host: string): boolean {
    if (!host || /[^a-zA-Z0-9_.\-\u00a0-\ud7ff\ue000-\ufdcf\ufdf0-\uffef]/u.test(host)) return false;
    // System.Uri checks the NFC copy using URI/IRI character rules, not DNS
    // grammar (e.g. U+037E becomes an allowed ';'). Use the original string for
    // label lengths and signing.
    const normalized = host.normalize('NFC');
    if (/[^\x21-\x7e\u00a0-\ud7ff\ue000-\ufdcf\ufdf0-\uffef]/u.test(normalized)
        || /["<>\\^`{|}\[\]]/u.test(normalized)) return false;
    const labels = host.split(/[.\u3002\uff0e\uff61]/);
    if (labels.at(-1) === '') labels.pop();
    if (labels.every(label => {
        if (!label || /^[\-_]/.test(label)) return false;
        const length = label.length + (/[^\x00-\x7f]/.test(label)
            ? 4 + [...label].filter(character => character.charCodeAt(0) > 0xff).length : 0);
        return length <= 63;
    })) return true;
    // System.Uri also accepts legacy UNC-style hosts up to 256 characters. Its
    // first two labels may start with '_'/'-'; later labels require a letter/digit.
    const unc = host.endsWith('.') ? host.slice(0, -1) : host;
    const [first, ...rest] = unc.split('.');
    return host.length <= 256 && /^[\p{L}0-9_-]+$/u.test(first!) && /[\p{L}_-]/u.test(first!)
        && rest.every((label, index) => (index === 0 ? /^[\p{L}0-9_-]+$/u : /^[\p{L}0-9][\p{L}0-9_-]*$/u).test(label));
}

function validIpv6Authority(authority: string): boolean {
    const match = /^\[([0-9a-fA-F:.]+)\](?::([0-9]*))?$/.exec(authority);
    if (!match || Number(match[2] ?? '') > 65535) return false;
    let address = match[1]!;
    if (address.includes('.')) {
        const colon = address.lastIndexOf(':');
        if (colon < 0) return false;
        const octets = address.slice(colon + 1).split('.');
        // System.Uri rejects 00 prefixes in every octet, but permits a single
        // leading zero in the final octet (e.g. 01 or 0255), interpreted as decimal.
        if (octets.length !== 4 || !octets.every((octet, index) => /^[0-9]+$/.test(octet)
            && Number(octet) <= 255 && !octet.startsWith('00') && (index === 3 || octet.length === 1 || octet[0] !== '0'))) return false;
        address = address.slice(0, colon + 1) + '0:0'; // Embedded IPv4 occupies two 16-bit groups.
    }
    const halves = address.split('::');
    if (halves.length > 2) return false;
    const groups = halves.flatMap(half => half ? half.split(':') : []);
    return groups.every(group => /^[0-9a-fA-F]{1,4}$/.test(group))
        && (halves.length === 2 ? groups.length < 8 : groups.length === 8);
}

function validatePathAndQuery(pathAndQuery: string): void {
    // .NET 10 IsWellFormedOriginalString rejects supplementary IRI characters in
    // the parsed path/query, including valid UTF-8 escapes that Uri expands there.
    // Inspect a temporary copy; non-IRI/private ranges keep their distinct rules.
    const tail = pathAndQuery.replace(/%(f[0-4])%([89ab][0-9a-f])%([89ab][0-9a-f])%([89ab][0-9a-f])/gi,
        (encoded, first: string, second: string, third: string, fourth: string) => {
            const scalar = ((parseInt(first, 16) & 7) << 18) | ((parseInt(second, 16) & 63) << 12)
                | ((parseInt(third, 16) & 63) << 6) | (parseInt(fourth, 16) & 63);
            return scalar >= 0x10000 && scalar <= 0x10ffff ? String.fromCodePoint(scalar) : encoded;
        });
    let query = false;
    for (const character of tail) {
        if (character === '?') query = true;
        const scalar = character.codePointAt(0)!;
        if (scalar > 0xffff && (scalar & 0xffff) < 0xfffe && (scalar < 0xe0000 || scalar >= 0xe1000) && (query || scalar < 0xf0000))
            throw new ProtocolError('invalid_push_gateway', 'The callback path or query contains an unsupported supplementary IRI character.');
    }
}

function validateAuthority(authority: string): void {
    try {
        if (authority.startsWith('[')) {
            if (!validIpv6Authority(authority)) throw new TypeError('Invalid HTTPS IPv6 host or port.');
        } else {
            const colon = authority.lastIndexOf(':');
            const host = colon < 0 ? authority : authority.slice(0, colon);
            const port = colon < 0 ? '' : authority.slice(colon + 1);
            if (!validHost(host) || !/^[0-9]*$/.test(port) || Number(port) > 65535) throw new TypeError('Invalid HTTPS host or port.');
        }
    } catch (cause) { throw new ProtocolError('invalid_push_gateway', 'Invalid push gateway URL.', { cause }); }
}

/** Validate an HTTPS callback of at most 512 UTF-8 bytes, preserving its signed spelling. */
export function validatePushGateway(gateway: string): void {
    if (typeof gateway !== 'string' || !gateway.startsWith('https://') || encodeUtf8(gateway).length > 512
        || /[\p{White_Space}\p{Cc}#\\]/u.test(gateway))
        throw new ProtocolError('invalid_push_gateway', 'Expected a complete HTTPS callback URL of at most 512 UTF-8 bytes, without whitespace, fragment, userinfo or backslash.');
    const originalAuthority = gateway.slice('https://'.length).split(/[/?]/)[0]!;
    if (!originalAuthority || originalAuthority.includes('@'))
        throw new ProtocolError('invalid_push_gateway', 'The push gateway requires a valid host without userinfo.');

    // Check a scoped IPv6 address independently of its opaque interface identifier.
    const scoped = /^https:\/\/\[([0-9a-fA-F:.]+)%[^/\]]*\]/.exec(gateway);
    const parseable = scoped ? `https://[${scoped[1]}]${gateway.slice(scoped[0].length)}` : gateway;
    const authority = parseable.slice('https://'.length).split(/[/?]/)[0]!;
    const pathAndQuery = parseable.slice('https://'.length + authority.length);
    if (authority.includes('%') || /[<>"^`{|}\[\]]/.test(pathAndQuery) || /%(?![0-9a-fA-F]{2})/.test(parseable))
        throw new ProtocolError('invalid_push_gateway', 'The push gateway contains an invalid host, escape or unescaped URL character.');
    validatePathAndQuery(pathAndQuery);
    validateAuthority(authority);
}
