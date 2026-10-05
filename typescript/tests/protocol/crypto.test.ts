import { expect, test } from 'vitest';
import {
    NetworkContext, agreeKey, decodeBase64Url, decryptAes, deriveKey, encryptAes,
    encryptionPublicKey, requireObject, signingInput, type JsonObject,
} from '@meshline/sdk';
import { unhex, vector } from '../support/vectors.js';

interface CryptoVectors {
    x25519: { cases: Array<{ name: string; private_key: string; peer_public_key: string; expected_raw_shared_secret: string; expected_accepted: boolean }> };
}

test.each(vector<CryptoVectors>('common').x25519.cases)('X25519 $name', row => {
    const agreement = (): Uint8Array => agreeKey(decodeBase64Url(row.private_key), decodeBase64Url(row.peer_public_key));
    if (row.expected_accepted) expect(agreement()).toEqual(decodeBase64Url(row.expected_raw_shared_secret));
    else expect(agreement).toThrow();
});

interface MessageVectors {
    network_context: string;
    payload: { encryption: {
        recipient_private_key: string; recipient_public_key: string;
        ephemeral_private_key: string; ephemeral_public_key: string; shared_secret: string;
        hkdf_salt: string; hkdf_info: string; wrap_key: string; key_box_aad: string;
        key_box_nonce: string; sealed_key: string; content_key: string; payload_nonce: string;
        plaintext_utf8_hex: string; ciphertext: string; envelope: JsonObject;
    }; aad: { utf8_hex: string } };
}

const message = vector<MessageVectors>('message-encryption');
const encryption = message.payload.encryption;

test('message key box unwrap matches independent keys and ciphertext', () => {
    expect(encryptionPublicKey(decodeBase64Url(encryption.recipient_private_key))).toEqual(decodeBase64Url(encryption.recipient_public_key));
    expect(encryptionPublicKey(decodeBase64Url(encryption.ephemeral_private_key))).toEqual(decodeBase64Url(encryption.ephemeral_public_key));
    const secret = agreeKey(decodeBase64Url(encryption.recipient_private_key), decodeBase64Url(encryption.ephemeral_public_key));
    expect(secret).toEqual(decodeBase64Url(encryption.shared_secret));
    // These two test metadata fields are explicitly standard base64, not wire base64url.
    const info = new Uint8Array(Buffer.from(encryption.hkdf_info, 'base64'));
    const aad = new Uint8Array(Buffer.from(encryption.key_box_aad, 'base64'));
    const key = deriveKey(secret, decodeBase64Url(encryption.hkdf_salt), info);
    expect(key).toEqual(decodeBase64Url(encryption.wrap_key));
    const box = decodeBase64Url(encryption.sealed_key);
    expect(box.subarray(0, 12)).toEqual(decodeBase64Url(encryption.key_box_nonce));
    expect(decryptAes(key, box.subarray(0, 12), box.subarray(12), aad)).toEqual(decodeBase64Url(encryption.content_key));
    expect(encryptAes(key, box.subarray(0, 12), decodeBase64Url(encryption.content_key), aad)).toEqual(box.subarray(12));
    const altered = new Uint8Array(box.subarray(12));
    altered[0] = altered[0]! ^ 1;
    expect(() => decryptAes(key, box.subarray(0, 12), altered, aad)).toThrow();
});

test('message payload decrypts with network-bound AAD', () => {
    const envelope = encryption.envelope;
    const input: JsonObject = {
        $type: 'meshline.message.aad', message_id: envelope.message_id!, created_at: envelope.created_at!,
        from: envelope.from!, from_device_id: envelope.from_device_id!, to: envelope.to!,
    };
    const context = NetworkContext.parse(message.network_context);
    const aad = signingInput(input, context);
    expect(aad).toEqual(unhex(message.payload.aad.utf8_hex));
    const key = decodeBase64Url(encryption.content_key);
    const nonce = decodeBase64Url(encryption.payload_nonce);
    const ciphertext = decodeBase64Url(encryption.ciphertext);
    const plaintext = unhex(encryption.plaintext_utf8_hex);
    expect(decryptAes(key, nonce, ciphertext, aad)).toEqual(plaintext);
    expect(encryptAes(key, nonce, plaintext, aad)).toEqual(ciphertext);
    const foreignAad = signingInput(input, new NetworkContext(context.reference + 1, context.registry));
    expect(() => decryptAes(key, nonce, ciphertext, foreignAad)).toThrow();
});
