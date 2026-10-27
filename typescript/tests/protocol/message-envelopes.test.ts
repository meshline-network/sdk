import { afterAll, beforeAll, describe, expect, test } from 'vitest';
import {
    NetworkContext, canonicalJson, decodeBase64Url, encodeBase64Url, groupMessageEnvelopeCodec, groupMessageEnvelopeInput,
    messageEnvelopeCodec, messageEnvelopeInput, requireObject, signDevice, validateGroupMessageEnvelope, validateMessageEnvelope, verifyDevice,
    type ExtensibleModel, type JsonObject, type JsonValue, type ProtocolCodec,
} from '@meshline/sdk';
import { DotnetBridge } from '../support/dotnet.js';
import { hex, vector, vectorValue } from '../support/vectors.js';

type Case = JsonObject & { name: string };
type Vectors = JsonObject & { network_context: string; notification: JsonObject & {
    variants: Case[]; field_cases: Case[]; signature_tampering: Case[]; retry_cases: Case[];
} };
let bridge: DotnetBridge;
beforeAll(() => { bridge = new DotnetBridge(); });
afterAll(async () => { await bridge.dispose(); });

function mutate(source: JsonValue, row: Case): JsonObject {
    const envelope = structuredClone(requireObject(source));
    for (const name of (row.remove ?? []) as string[]) delete envelope[name];
    Object.assign(envelope, row.set ?? {});
    return envelope;
}

function envelopeCases<T extends ExtensibleModel & { readonly notify?: boolean; readonly deviceSignature: Uint8Array }>(
    topic: string, codec: ProtocolCodec<T>, input: (value: T, context: NetworkContext) => Uint8Array, validate: (value: T) => void,
) {
    describe(topic, () => {
        const root = vector<Vectors>(topic); const cases = root.notification; const context = NetworkContext.parse(root.network_context);
        const privateKey = decodeBase64Url(vectorValue(root, cases, 'signing_private_key') as string);
        const publicKey = decodeBase64Url(vectorValue(root, cases, 'signing_public_key') as string);
        const openInDotnet = (wire: JsonObject) => bridge.invoke<{ valid: boolean; json: string; signingInput: string }>({
            operation: 'message-envelope', context: context.toString(), json: canonicalJson(wire), publicKey: encodeBase64Url(publicKey),
        });

        test.each(cases.variants)('fixed signature and C# round trip: $name', async row => {
            const wire = requireObject(vectorValue(root, row, 'envelope')); const envelope = codec.decode(wire); validate(envelope);
            expect(envelope.additionalProperties ?? {}).not.toHaveProperty('notify');
            expect(envelope.notify).toBe(wire.notify); expect(envelope.notify === true).toBe(row.expected_allows_push);
            expect(codec.encode(envelope)).toEqual(wire);
            const signing = input(envelope, context); expect(hex(signing)).toBe(vectorValue(root, row, 'signing_input_utf8_hex'));
            expect(signDevice(signing, privateKey)).toEqual(envelope.deviceSignature); expect(verifyDevice(signing, envelope.deviceSignature, publicKey)).toBe(true);
            const other = await openInDotnet(wire);
            expect(other).toEqual({ valid: true, json: codec.stringify(envelope), signingInput: encodeBase64Url(signing) });
            expect(codec.encode(codec.parse(other.json))).toEqual(wire);
        });

        test.each(cases.field_cases)('field representation in both SDKs: $name', async row => {
            const wire = mutate(vectorValue(root, cases, 'base_envelope'), row);
            if (!row.expected_format_valid) {
                expect(() => codec.decode(wire)).toThrow(); await expect(openInDotnet(wire)).rejects.toThrow(); return;
            }
            const envelope = codec.decode(wire); validate(envelope);
            expect(envelope.notify === true).toBe(row.expected_allows_push); expect(codec.encode(envelope)).toEqual(wire);
            expect((await openInDotnet(wire)).json).toBe(codec.stringify(envelope));
        });

        test.each(cases.signature_tampering)('notification tampering invalidates the signature: $name', async row => {
            const wire = mutate(vectorValue(root, row, 'envelope'), row); const envelope = codec.decode(wire); validate(envelope);
            expect(verifyDevice(input(envelope, context), envelope.deviceSignature, publicKey)).toBe(row.expected_signature_valid);
            expect((await openInDotnet(wire)).valid).toBe(row.expected_signature_valid);
        });

        test.each(cases.retry_cases)('canonical retry identity: $name', row => {
            const accepted = codec.decode(vectorValue(root, row, 'accepted_envelope'));
            const retry = codec.decode(vectorValue(root, row, 'retry_envelope'));
            expect(codec.stringify(accepted) === codec.stringify(retry)).toBe(row.expected_result === 'original_result');
        });
    });
}

envelopeCases('message-encryption', messageEnvelopeCodec, messageEnvelopeInput, validateMessageEnvelope);
envelopeCases('groups', groupMessageEnvelopeCodec, groupMessageEnvelopeInput, validateGroupMessageEnvelope);
