import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { runPortableVectors, type VectorFile } from '../expo/portable-vectors.js';
import { vectorDirectory } from '../support/vectors.js';

function files(): Record<string, VectorFile> {
    const manifest = JSON.parse(readFileSync(new URL('manifest.json', vectorDirectory), 'utf8')) as { files: Record<string, string> };
    return Object.fromEntries(Object.entries(manifest.files).map(([name, sha256]) => [name, { source: readFileSync(new URL(name, vectorDirectory), 'utf8'), sha256 }]));
}
test('the exact portable native-acceptance runner passes its independent fixture subset on Node', async () => {
    let yields = 0; const report = await runPortableVectors(files(), undefined, async () => { yields++; });
    expect(report.results.filter(value => !value.passed)).toEqual([]); expect(report.passed).toBeGreaterThan(100); expect(yields).toBe(report.results.length);
    expect(report.results.filter(value => value.name.startsWith('fixture SHA-256:'))).toHaveLength(7);
});

test('the portable runner uses its injected signing randomness when Web Crypto is absent', async () => {
    // A fresh process matches Hermes startup: Noble probes RNG availability at
    // module initialization. Removing it after the probe models a different case.
    const script = `
        import { randomFillSync } from 'node:crypto';
        import { readFileSync } from 'node:fs';
        Object.defineProperty(globalThis, 'crypto', { value: undefined });
        const { runPortableVectors } = await import(process.argv[1]);
        const requests = [];
        const report = await runPortableVectors(JSON.parse(readFileSync(0, 'utf8')), undefined, undefined,
            { bytes(length) { requests.push(length); return randomFillSync(new Uint8Array(length)); } });
        console.log(JSON.stringify({ failures: report.results.filter(value => !value.passed), passed: report.passed, requests }));
    `;
    const result = JSON.parse(execFileSync(process.execPath, ['--input-type=module', '--eval', script,
        new URL('../expo/portable-vectors.ts', import.meta.url).href], { input: JSON.stringify(files()), encoding: 'utf8', windowsHide: true }));
    expect(result).toEqual({ failures: [], passed: 214, requests: [32] });
});
test('fixture integrity failure prevents protocol checks from using changed expected answers', async () => {
    const input = files(); input['common-v1.json'] = { ...input['common-v1.json']!, source: input['common-v1.json']!.source + ' ' };
    const report = await runPortableVectors(input); expect(report.failed).toBe(1); expect(report.results).toHaveLength(7);
});
test('a cryptographic failure stays failed while later independent checks still run', async () => {
    const input = files(); const fixture = JSON.parse(input['identity-auth-v1.json']!.source) as { device_certificate: { account_signature: string } };
    fixture.device_certificate.account_signature = Buffer.alloc(64).toString('base64url'); const source = JSON.stringify(fixture);
    input['identity-auth-v1.json'] = { source, sha256: createHash('sha256').update(source).digest('hex') };
    const report = await runPortableVectors(input); expect(report.failed).toBe(1);
    expect(report.results.find(value => value.name.startsWith('Neo identity'))).toMatchObject({ passed: false });
    expect(report.results.at(-1)).toMatchObject({ name: 'group message AAD, ciphertext and signature', passed: true });
});
