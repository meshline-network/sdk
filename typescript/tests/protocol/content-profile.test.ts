import { expect, test } from 'vitest';
import {
    accountProfileCodec, contentReferenceCodec, contentEncryptionAad, contentHashBytes, contentHashUri, decryptContent, encryptContent,
    decodeBase64Url, containsNonWhitespace, messageBodyCodec, resolveContentReference, validateMessageBody, validateMediaType,
    validateContentReference, validateProfile, type AccountProfile, type JsonObject, type JsonValue,
} from '@meshline/sdk';
import { vector, hex, unhex } from '../support/vectors.js';

const rows = vector<{
    body: { body_cases: { name: string; body: JsonObject }[]; invalid_body_cases: { name: string; body: JsonObject }[] };
    attachment_encryption: { reference: JsonObject; plaintext_utf8_hex: string; aad_utf8_hex: string; ciphertext_and_tag: string };
    hash_references: { hash_cases: { name: string; value: JsonValue; valid: boolean }[];
        resolution_cases: { name: string; uri: string; attachments: JsonObject[]; expected: { status: string; index?: number } }[] };
}>('message-content');
test.each(rows.body.body_cases)('body vector: $name', row => { validateMessageBody(messageBodyCodec.decode(row.body)); });
test.each(rows.body.invalid_body_cases)('invalid body vector: $name', row => { expect(() => validateMessageBody(messageBodyCodec.decode(row.body))).toThrow(); });
test.each(rows.hash_references.hash_cases)('content hash vector: $name', row => {
    const validate = () => contentHashBytes(row.value as string);
    if (row.valid) expect(validate()).toHaveLength(32); else expect(validate).toThrow();
});
test.each(rows.hash_references.resolution_cases)('content reference resolution vector: $name', row => {
    if (row.expected.status === 'invalid_attachments') {
        expect(() => resolveContentReference(row.uri, row.attachments.map(value => contentReferenceCodec.decode(value)))).toThrow();
        return;
    }
    const attachments = row.attachments.map(value => contentReferenceCodec.decode(value));
    expect(resolveContentReference(row.uri, attachments)).toBe(row.expected.status === 'resolved' ? attachments[row.expected.index!] : undefined);
});
test('attachment encryption matches independent exact AAD/ciphertext and verifies plaintext hash', () => {
    const row = rows.attachment_encryption; const reference = contentReferenceCodec.decode(row.reference); validateContentReference(reference);
    const plaintext = unhex(row.plaintext_utf8_hex); const ciphertext = decodeBase64Url(row.ciphertext_and_tag);
    expect(hex(contentEncryptionAad(reference))).toBe(row.aad_utf8_hex);
    expect(encryptContent(reference, plaintext)).toEqual(ciphertext); expect(decryptContent(reference, ciphertext)).toEqual(plaintext);
    expect(contentHashUri(reference)).toBe(`ni:///sha-256;${reference.hash.slice(7)}`);
    expect(() => decryptContent({ ...reference, contentType: 'image/png' }, ciphertext)).toThrow();
    expect(() => encryptContent(reference, new Uint8Array(reference.size))).toThrow();
    expect(decryptContent({ ...reference, uri: 'https://elsewhere.example/file?signed=opaque' }, ciphertext)).toEqual(plaintext);
});
test('media types parse quoted semicolons/escapes and inspect every charset', () => {
    validateMediaType('Text/Plain; label="a;b"; charset="u\\tf-8"; CHARSET=UTF-8', true);
    for (const value of ['text/plain; charset=utf-8; charset=latin1', 'text/plain; charset=""', 'text/plain\n', 'text/plain;bad', 'text/plain; x="unterminated', 'text/plain; charset=utf-8 garbage'])
        expect(() => validateMediaType(value, true)).toThrow();
});
const profile: AccountProfile = { account: 'neo:860833102:NgaxELHoZFpQWNwd74Fvq4wF3qz57WTfpp', publicDiscovery: false, updatedAt: 1730000000, deviceSignature: new Uint8Array(64) };
test('profile limits count UTF-8 and the full signed document, including extensions', () => {
    validateProfile({ ...profile, nickname: '中'.repeat(85), bio: '' });
    expect(() => validateProfile({ ...profile, nickname: '中'.repeat(86) })).toThrow();
    expect(() => validateProfile({ ...profile, bio: 'x'.repeat(2049) })).toThrow();
    expect(() => validateProfile({ ...profile, additionalProperties: { future: 'x'.repeat(8192) } })).toThrow();
    const document = accountProfileCodec.encode(profile); expect(Object.hasOwn(document, 'nickname')).toBe(false);
    expect(() => accountProfileCodec.decode({ ...document, nickname: null })).toThrow();
    expect(() => validateProfile({ ...profile, avatar: contentReferenceCodec.decode(rows.attachment_encryption.reference) })).toThrow('image');
});
test('text whitespace follows protocol code points rather than JavaScript trim', () => {
    for (const text of ['\u0085', '\u2000\u2001\u200a', '\t\r\n\u3000']) {
        expect(containsNonWhitespace(text)).toBe(false); expect(() => validateProfile({ ...profile, nickname: text })).toThrow();
    }
    for (const text of ['\u200b', '\ufeff', 'a\u0085']) { expect(containsNonWhitespace(text)).toBe(true); validateProfile({ ...profile, nickname: text }); }
});
