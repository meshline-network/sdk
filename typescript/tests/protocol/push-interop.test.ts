import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { URL as ExpoUrl } from 'whatwg-url-minimum';
import {
    NetworkContext, certificateAccountInput, certificateDeviceInput, certificateId, decodeBase64Url,
    deviceCertificateCodec, signAccount, signDevice, validateCertificate, type JsonObject,
} from '@meshline/sdk';
import { DotnetBridge } from '../support/dotnet.js';
import { vector } from '../support/vectors.js';

const source = vector<{
    network_context: string;
    keys: { certificate_account_private_key: string; certificate_device_private_key: string };
    device_certificates: { name: string; object: JsonObject }[];
}>('push-notifications');
const context = NetworkContext.parse(source.network_context);
let bridge: DotnetBridge;
beforeAll(() => { bridge = new DotnetBridge(); });
afterAll(async () => { await bridge.dispose(); });

function signedCertificate(pushGateway?: string) {
    let certificate = { ...deviceCertificateCodec.decode(source.device_certificates[0]!.object), ...(pushGateway === undefined ? {} : { pushGateway }) };
    certificate = { ...certificate, deviceSignature: signDevice(certificateDeviceInput(certificate, context), decodeBase64Url(source.keys.certificate_device_private_key)) };
    return { ...certificate, accountSignature: signAccount(certificateAccountInput(certificate, context), decodeBase64Url(source.keys.certificate_account_private_key)) };
}

test.each(source.device_certificates)('fixed push certificate $name validates and round-trips through C# and TypeScript', async value => {
    const certificate = deviceCertificateCodec.decode(value.object);
    const json = deviceCertificateCodec.stringify(certificate);
    const result = await bridge.invoke<{ valid: boolean; json: string; deviceId: string }>({ operation: 'certificate', context: context.toString(), json });
    expect(result.valid).toBe(true);
    expect(result.json).toBe(json);
    expect(result.deviceId).toBe(certificateId(certificate, context));
    validateCertificate(deviceCertificateCodec.parse(result.json), context);
});

test.each([undefined, 'https://PUSH.example.com:443/callback?', 'https://push.example.com?q=chat@meshline',
    'https://例子.测试/推送?应用=聊天', 'https://push.example.com/' + '界'.repeat(160),
    'https://push.example.com/#', 'https://@push.example.com/', 'https:///callback', 'https://push.example.com/%xy',
    'HTTPS://push.example.com/', 'https://push.example.com:65536/', 'https://push.example.com/\u0085',
    ...['^', '|', '"', '<', '>', '{', '}', '[', ']', '`'].flatMap(value => [`https://push.example.com/${value}`, `https://push.example.com/?q=${value}`]),
    'https://push.example.com/%5Bescaped%5D?q=%7Bescaped%7D', 'https://push.example.com/\uFEFF',
    'https://[fe80::1%25eth0]/callback', 'https://[fe80::1%eth0]/callback', 'https://[::ffff:192.168.1.1%25eth0]/callback',
    ...['256.256.256.256', 'example.1', '12345678901234567890', '1.2.3.09', '1..2', 'foo..', '.foo', 'foo_bar', '-foo', 'foo-',
        'foo:000000000000000443', 'foo:', 'foo.', '\u200D.foo', 'a\uFEFFb.com', 'foo!bar', 'foo,bar', 'foo;bar', "foo'bar", 'foo(bar)', 'foo~bar',
        'foo.-bar', '_foo._bar', '_foo.foo._bar', 'foo。bar', 'foo。。bar', 'a'.repeat(64), 'a'.repeat(256), 'a'.repeat(257), '1'.repeat(64),
        '\u00AD.example', '\uE000.example', '\uFDD0.example', '\uFDEF.example', '\uFFFF.example', '\u{1F600}.example', '[fe80::1%abc/def]'].map(host => `https://${host}/`),
    ...[0x10000, 0x1f600, 0x1fffe, 0xe0000, 0xe1000, 0xf0000, 0x10ffff].flatMap(scalar => [
        `https://foo/${String.fromCodePoint(scalar)}`, `https://foo/?q=${String.fromCodePoint(scalar)}`,
    ]),
    'https://foo/%F0%9F%98%80?q=%F0%9F%98%80',
])('newly signed gateway %j has the same acceptance and DeviceId in both SDKs', async pushGateway => {
    const certificate = signedCertificate(pushGateway);
    let valid = true;
    try { validateCertificate(certificate, context); } catch { valid = false; }
    const json = deviceCertificateCodec.stringify(certificate);
    const result = await bridge.invoke<{ valid: boolean; deviceId: string; json: string }>({ operation: 'certificate', context: context.toString(), json });
    expect(result.valid).toBe(valid);
    expect(result.deviceId).toBe(certificateId(certificate, context));
    expect(result.json).toBe(json);
});

// Exercise Expo's actual URL implementation on Node; this is not a native runtime test.
describe.each([{ runtime: 'Node', url: URL }, { runtime: 'Expo', url: ExpoUrl }])('$runtime push gateway authorities', ({ url }) => {
    beforeEach(() => { vi.stubGlobal('URL', url); });
    afterEach(() => { vi.unstubAllGlobals(); });

    describe.each<{ kind: string; cases: readonly (readonly [string, boolean])[] }>([
        { kind: 'IPv6', cases: [
            ['[::]', true], ['[::1]', true], ['[2001:DB8:0:0:0:0:0:1]', true],
            ['[1:2:3:4:5:6:7::]', true], ['[1:2:3:4:5:6:7:8]', true],
            ['[::ffff:192.168.1.1]', true], ['[1:2:3:4:5:6:192.168.1.1]', true],
            ['[::ffff:0.0.0.0]', true], ['[::ffff:255.255.255.255]', true],
            ['[::ffff:192.168.1.01]', true], ['[::ffff:192.168.1.010]', true],
            ['[::ffff:192.168.1.099]', true], ['[::ffff:192.168.1.0255]', true],
            ['[::ffff:192.168.1.01%25eth0]', true],
            ['[::FFFF:192.168.1.1%25eth0]:000000443', true], ['[fe80::1%eth0]:65535', true],
            ['[::1]:', true], ['[::1]:0', true],
            ['[::ffff:192.168.001.1]', false], ['[::ffff:192.168.1.001]', false],
            ['[::ffff:192.168.1.00]', false], ['[::ffff:192.168.1.0256]', false],
            ['[::ffff:0192.168.1.1]', false], ['[::ffff:192.0168.1.1]', false],
            ['[::ffff:00.0.0.0]', false], ['[::ffff:192.168.001.1%25eth0]:000000443', false],
            ['[::ffff:256.168.1.1]', false], ['[::ffff:192.168.1]', false],
            ['[::ffff:192.168.1.1.1]', false], ['[::ffff:192.168.1.1:1]', false],
            ['[::ffff:192.168.1.1::]', false], ['[1:2:3:4:5:192.168.1.1]', false],
            ['[1:2:3:4:5:6::192.168.1.1]', false], ['[192.168.1.1]', false],
            ['[]', false], ['[1:2:3:4:5:6:7]', false], ['[1:2:3:4:5:6:7:8:9]', false],
            ['[1:2:3:4:5:6:7:8::]', false], ['[1::2::3]', false], ['[:::1]', false],
            ['[:1:2:3:4:5:6:7]', false], ['[1:2:3:4:5:6:7:]', false],
            ['[::10000]', false], ['[::g]', false], ['[::1', false], ['[::1]extra', false],
            ['[::1]:65536', false], ['[::1]:-1', false], ['[::1]:abc', false],
        ] as const },
        { kind: 'Unicode hosts', cases: [
            ...['\u1fef', '\ufa6c', '\ufacf', '\ufad0', '\ufad1', '\ufad5', '\ufad6', '\ufad7'].map(character => [`a${character}b.example`, false] as const),
            ['cafe\u0301.example', true], ['a\u212bb.example', true], ['a\u0344b.example', true],
            ['a\u037eb.example', true], ['a;b.example', false],
            // Normalization can expand a label; System.Uri applies length limits to the original host.
            ['q\u0344'.repeat(17) + '.example', true], ['\u0344'.repeat(19) + '.example', true],
        ] as const },
    ])('$kind', ({ cases }) => {
        test.each(cases)('signed authority %s has expected validity %s in TypeScript and C#', async (authority, valid) => {
            const pushGateway = `https://${authority}/callback?app=meshline`;
            const certificate = signedCertificate(pushGateway);
            if (valid) validateCertificate(certificate, context);
            else expect(() => validateCertificate(certificate, context)).toThrow(expect.objectContaining({ code: 'invalid_push_gateway' }));
            const json = deviceCertificateCodec.stringify(certificate);
            const result = await bridge.invoke<{ valid: boolean; deviceId: string; json: string }>({ operation: 'certificate', context: context.toString(), json });
            expect(result.valid).toBe(valid);
            expect(result.deviceId).toBe(certificateId(certificate, context));
            expect(result.json).toBe(json);
            expect(deviceCertificateCodec.parse(result.json).pushGateway).toBe(pushGateway);
        });
    });
});
