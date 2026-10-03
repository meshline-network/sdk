import { decryptAes, encryptAes, requireLength, sha256 } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { bytes, defineCodec, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import { decodeBase64Url, encodeUtf8, equalBytes } from '../protocol/encoding.js';
import { canonicalBytes, requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { parseAbsoluteUrl } from '../runtime/url.js';

/** Protocol Unicode 17 White_Space, deliberately independent of JavaScript trim(). */
export function containsNonWhitespace(value: string): boolean { return /[^\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/u.test(value); }
const token = "[!#$%&'*+.^_`|~0-9A-Za-z-]+";
const quoted = '"(?:[\\t\\x20\\x21\\x23-\\x5B\\x5D-\\x7E\\x80-\\xFF]|\\\\[\\t\\x20-\\x7E\\x80-\\xFF])*"';
const mediaStart = new RegExp(`^${token}/${token}`);
const mediaParameter = new RegExp(`[ \\t]*;[ \\t]*(?:(${token})=(${token}|${quoted}))?`, 'y');
/** RFC 9110 grammar; repeated charset parameters must all specify UTF-8 for bodies. */
export function validateMediaType(value: string, requireUtf8 = false): void {
    if (typeof value !== 'string') throw new ProtocolError('invalid_media_type', 'Expected a media type.');
    const start = mediaStart.exec(value);
    if (!start) throw new ProtocolError('invalid_media_type', 'Invalid media type.');
    let offset = start[0].length;
    while (offset < value.length) {
        mediaParameter.lastIndex = offset;
        const parameter = mediaParameter.exec(value);
        if (!parameter) throw new ProtocolError('invalid_media_type', 'Invalid media type parameter.');
        if (requireUtf8 && parameter[1]?.toLowerCase() === 'charset') {
            const raw = parameter[2]!;
            const charset = raw.startsWith('"') ? raw.slice(1, -1).replace(/\\([\s\S])/g, '$1') : raw;
            if (charset.toLowerCase() !== 'utf-8') throw new ProtocolError('invalid_charset', 'Message body charset must be UTF-8.');
        }
        offset = mediaParameter.lastIndex;
    }
}
export interface ContentEncryption extends ExtensibleModel { readonly alg: string; readonly key: Uint8Array; readonly nonce: Uint8Array }
export const contentEncryptionCodec = defineCodec<ContentEncryption>({ alg: { wire: 'alg', codec: text }, key: { wire: 'key', codec: bytes }, nonce: { wire: 'nonce', codec: bytes } });
export function validateContentEncryption(value: ContentEncryption): void {
    validateCompleteObject(contentEncryptionCodec.encode(value));
    if (value.alg !== 'AES-256-GCM') throw new ProtocolError('unsupported_algorithm', 'Content encryption must use AES-256-GCM.');
    requireLength(value.key, 32, 'Content key'); requireLength(value.nonce, 12, 'Content nonce');
}
export interface ContentReference extends ExtensibleModel {
    readonly uri: string; readonly hash: string; readonly contentType: string; readonly size: number; readonly encryption?: ContentEncryption;
}
export const contentReferenceCodec = defineCodec<ContentReference>({ uri: { wire: 'uri', codec: text }, hash: { wire: 'hash', codec: text },
    contentType: { wire: 'content_type', codec: text }, size: { wire: 'size', codec: integer }, encryption: { wire: 'encryption', codec: contentEncryptionCodec, optional: true } });
export function contentHashBytes(hash: string): Uint8Array {
    if (typeof hash !== 'string' || !hash.startsWith('sha256:')) throw new ProtocolError('invalid_hash', 'Expected a sha256 content hash.');
    const bytes = decodeBase64Url(hash.slice(7)); requireLength(bytes, 32, 'Content hash'); return bytes;
}
export function validateContentReference(value: ContentReference): void {
    validateCompleteObject(contentReferenceCodec.encode(value));
    encodeUtf8(value.uri);
    if (!/^https:\/\//i.test(value.uri) || /[\s\u0085\x00-\x1f\x7f-\x9f\\]/u.test(value.uri) || /%(?![0-9a-fA-F]{2})/.test(value.uri))
        throw new ProtocolError('invalid_uri', 'Content requires a well-formed absolute HTTPS URI.');
    try { if (!parseAbsoluteUrl(value.uri).hostname) throw new Error('Missing host'); }
    catch (cause) { throw new ProtocolError('invalid_uri', 'Content requires a valid HTTPS host.', { cause }); }
    contentHashBytes(value.hash); requireSafeInteger(value.size, 0); validateMediaType(value.contentType);
    if (value.encryption) validateContentEncryption(value.encryption);
    contentReferenceCodec.encode(value);
}
export function contentHashUri(value: ContentReference): string { contentHashBytes(value.hash); return `ni:///sha-256;${value.hash.slice(7)}`; }
export function validateAttachments(attachments: readonly ContentReference[]): void {
    const hashes = new Set<string>();
    for (const reference of attachments) {
        validateContentReference(reference);
        if (hashes.has(reference.hash)) throw new ProtocolError('duplicate_attachment', 'Attachments cannot contain duplicate plaintext hashes.');
        hashes.add(reference.hash);
    }
}
export function resolveContentReference(uri: string, attachments: readonly ContentReference[]): ContentReference | undefined {
    validateAttachments(attachments);
    if (!/^ni:\/\/\//i.test(uri) || /[/?#]/.test(uri.slice(6))) return undefined;
    const separator = uri.indexOf(';', 6); if (separator < 0) return undefined;
    try {
        if (decodeURIComponent(uri.slice(6, separator)) !== 'sha-256') return undefined;
        const hash = `sha256:${decodeURIComponent(uri.slice(separator + 1))}`; contentHashBytes(hash);
        return attachments.find(value => value.hash === hash);
    } catch { return undefined; } // Malformed user-facing ni links are simply unresolved.
}
export function verifyContent(value: ContentReference, plaintext: Uint8Array): void {
    if (plaintext.length !== value.size || !equalBytes(sha256(plaintext), contentHashBytes(value.hash)))
        throw new ProtocolError('invalid_content', 'Plaintext does not match the content size and hash.');
}
export function contentEncryptionAad(value: ContentReference): Uint8Array {
    if (!value.encryption) throw new ProtocolError('encryption_required', 'Content reference has no encryption parameters.');
    validateContentReference(value);
    return canonicalBytes({ $type: 'meshline.content.reference.aad', alg: value.encryption.alg, hash: value.hash, content_type: value.contentType, size: value.size });
}
/** Low-level helper; the caller supplies a fresh independent key and nonce for every attachment. */
export function encryptContent(value: ContentReference, plaintext: Uint8Array): Uint8Array {
    const aad = contentEncryptionAad(value); verifyContent(value, plaintext);
    return encryptAes(value.encryption!.key, value.encryption!.nonce, plaintext, aad);
}
export function decryptContent(value: ContentReference, ciphertext: Uint8Array): Uint8Array {
    const aad = contentEncryptionAad(value);
    const plaintext = decryptAes(value.encryption!.key, value.encryption!.nonce, ciphertext, aad);
    try { verifyContent(value, plaintext); return plaintext; } catch (error) { plaintext.fill(0); throw error; }
}
export interface MessageBody extends ExtensibleModel { readonly contentType: string; readonly text: string }
export const messageBodyCodec = defineCodec<MessageBody>({ contentType: { wire: 'content_type', codec: text }, text: { wire: 'text', codec: text } });
export function validateMessageBody(value: MessageBody): void {
    validateCompleteObject(messageBodyCodec.encode(value));
    validateMediaType(value.contentType, true); encodeUtf8(value.text);
    if (!containsNonWhitespace(value.text)) throw new ProtocolError('invalid_body', 'Message body must contain non-whitespace text.');
    messageBodyCodec.encode(value);
}
