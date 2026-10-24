import { expect, test } from 'vitest';
import {
    NetworkContext, certificateAccountInput, certificateDeviceInput, certificateId, decodeBase64Url,
    deviceCertificateCodec, encodeUtf8, requireObject, validateCertificate, verifyAccount, verifyDevice,
    type JsonObject, type JsonValue,
} from '@meshline/sdk';
import { hex, vector } from '../support/vectors.js';

interface SigningInput { canonical_json: string; utf8_hex: string }
interface CertificateVector { name: string; object: JsonObject; device_id: string; device_signing_input: SigningInput; account_signing_input: SigningInput }
interface PushVectors {
    network_context: string;
    device_certificates: CertificateVector[];
    certificate_tampering: { name: string; certificate_ref: string; set: JsonObject; remove: string[]; expected_device_signature_valid: boolean; expected_account_signature_valid: boolean }[];
    gateway_urls: { name: string; value: JsonValue; expected_format_valid: boolean }[];
}
const vectors = vector<PushVectors>('push-notifications');
const context = NetworkContext.parse(vectors.network_context);

test.each(vectors.device_certificates)('push certificate $name matches fixed signing inputs, signatures and DeviceId', value => {
    const certificate = deviceCertificateCodec.decode(value.object);
    validateCertificate(certificate, context);
    expect(certificateId(certificate, context)).toBe(value.device_id);
    expect(certificate.additionalProperties ?? {}).not.toHaveProperty('push_gateway');
    for (const [expected, actual] of [
        [value.device_signing_input, certificateDeviceInput(certificate, context)],
        [value.account_signing_input, certificateAccountInput(certificate, context)],
    ] as const) {
        expect(hex(actual)).toBe(expected.utf8_hex);
        expect(actual).toEqual(encodeUtf8(expected.canonical_json));
    }
    expect(deviceCertificateCodec.encode(certificate)).toEqual(value.object);
    expect(() => validateCertificate(certificate, new NetworkContext(context.reference + 1, context.registry))).toThrow();
});

test.each(vectors.certificate_tampering)('$name invalidates both signatures without changing identity', value => {
    let source = vectors as unknown as JsonValue;
    for (const segment of value.certificate_ref.split('/').slice(1)) source = Array.isArray(source) ? source[Number(segment)]! : requireObject(source)[segment]!;
    const original = deviceCertificateCodec.decode(source);
    const modified = { ...requireObject(source), ...value.set };
    for (const key of value.remove) delete modified[key];
    const certificate = deviceCertificateCodec.decode(modified);
    expect(verifyDevice(certificateDeviceInput(certificate, context), certificate.deviceSignature, certificate.signingPublicKey)).toBe(value.expected_device_signature_valid);
    expect(verifyAccount(certificateAccountInput(certificate, context), certificate.accountSignature, certificate.accountPublicKey)).toBe(value.expected_account_signature_valid);
    expect(() => validateCertificate(certificate, context)).toThrow(expect.objectContaining({ code: 'invalid_signature' }));
    expect(certificateId(certificate, context)).toBe(certificateId(original, context));
});

test.each(vectors.gateway_urls)('gateway format $name matches the protocol without rewriting the string', value => {
    const wire = { ...vectors.device_certificates[0]!.object, push_gateway: value.value };
    if (value.value === null) {
        expect(() => deviceCertificateCodec.decode(wire)).toThrow();
        return;
    }
    const certificate = deviceCertificateCodec.decode(wire);
    expect(certificate.pushGateway).toBe(value.value);
    expect(() => validateCertificate(certificate, context)).toThrow(expect.objectContaining({ code: value.expected_format_valid ? 'invalid_signature' : 'invalid_push_gateway' }));
});

test.each(['HTTPS://push.example.com/callback', 'https://@push.example.com/callback', 'https://push.example.com:65536/callback',
    'https://push.example.com/\u0000', 'https://push.example.com/\u0085', 'https://push.example.com/%xy', 'https://push.example.com/\ud800'])('rejects malformed gateway %j', pushGateway => {
    const certificate = { ...deviceCertificateCodec.decode(vectors.device_certificates[0]!.object), pushGateway };
    expect(() => validateCertificate(certificate, context)).toThrow();
});

test('gateway length counts UTF-8 bytes and does not normalize query, path, host or port', () => {
    const pushGateway = 'https://PUSH.example.com:443/?q=' + '界'.repeat(160);
    expect(encodeUtf8(pushGateway)).toHaveLength(512);
    const certificate = { ...deviceCertificateCodec.decode(vectors.device_certificates[0]!.object), pushGateway };
    expect(() => validateCertificate(certificate, context)).toThrow(expect.objectContaining({ code: 'invalid_signature' }));
    expect(deviceCertificateCodec.parse(deviceCertificateCodec.stringify(certificate)).pushGateway).toBe(pushGateway);
    expect(() => validateCertificate({ ...certificate, pushGateway: pushGateway + '界' }, context)).toThrow(expect.objectContaining({ code: 'invalid_push_gateway' }));
    expect(certificate.accountSignature).toEqual(decodeBase64Url(vectors.device_certificates[0]!.object.account_signature as string));
});
