import { toASCII } from 'tr46';

/** Normalize domain names before the platform parser, including Expo's ASCII-only URL. */
export function parseAbsoluteUrl(input: string): URL {
    const match = /^([a-z][a-z0-9+.-]*:\/\/)([^/?#]*)([\s\S]*)$/i.exec(input);
    if (!match) return new URL(input);
    const authority = match[2]!; const at = authority.lastIndexOf('@');
    const credentials = authority.slice(0, at + 1); const address = authority.slice(at + 1);
    if (!address || address.startsWith('[')) return new URL(input); // IPv6 belongs to the URL parser.
    const colon = address.lastIndexOf(':'); const port = colon === -1 ? '' : address.slice(colon);
    const host = decodeURIComponent(colon === -1 ? address : address.slice(0, colon));
    const ascii = toASCII(host, { checkBidi: true, checkJoiners: true, checkHyphens: false,
        transitionalProcessing: false, useSTD3ASCIIRules: false, verifyDNSLength: false });
    // Prevent escaped or mapped separators from changing authority boundaries.
    if (!ascii || /[\u0000-\u0020\u007f#%/:<>?@[\\\]^|]/.test(ascii)) throw new TypeError('Invalid international domain name.');
    return new URL(match[1]! + credentials + ascii + port + match[3]!);
}
