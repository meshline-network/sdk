import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, symlink, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, expect, test } from 'vitest';
import { removeTestDirectory } from '../support/temp.js';

const command = fileURLToPath(new URL('../../packages/expo/compatibility/apply-android-fix.mjs', import.meta.url));
const requireExpo = createRequire(new URL('../../packages/expo/package.json', import.meta.url));
const sourceName = 'android/src/main/java/expo/modules/kotlin/sharedobjects/SharedObjectRegistry.kt';
const original = await readFile(join(dirname(requireExpo.resolve('expo-modules-core/package.json')), sourceName));
const httpSourceName = 'android/src/main/java/expo/modules/fetch/NativeRequest.kt';
const httpOriginal = await readFile(join(dirname(requireExpo.resolve('expo/package.json')), httpSourceName));
const httpOriginalHash = '4e35ef082ef083490e7088d732af787c348678bc653deab3ef646ef9c1707938';
const httpFixedHash = '6fc9d773e12eb1788cde5392c666e495131240b2240657c85664de02aa3fc458';
const originalHash = 'c41c2a8aab60bef0476b2f805aa863507b96144701d782a228e76c1afbf1b4b3';
const fixedHash = 'b9417c719e9cdd92c1f08ba8d841e4b9cbfd933dd031b7b2e3a8ff44b9861df4';
const hash = (value: Uint8Array) => createHash('sha256').update(value).digest('hex');
const directories: string[] = [];
afterEach(async () => { for (const path of directories.splice(0)) await removeTestDirectory(path); });
async function fixture(version = '57.0.19', expoVersion = '57.0.25') {
    const app = await mkdtemp(join(tmpdir(), 'meshline-expo-compat-')); directories.push(app);
    const core = join(app, 'node_modules/expo-modules-core'); const source = join(core, sourceName);
    await mkdir(dirname(source), { recursive: true });
    await writeFile(join(core, 'package.json'), JSON.stringify({ name: 'expo-modules-core', version }));
    await writeFile(source, original);
    const expo = join(app, 'node_modules/expo'); const httpSource = join(expo, httpSourceName);
    await mkdir(dirname(httpSource), { recursive: true });
    await writeFile(join(expo, 'package.json'), JSON.stringify({ name: 'expo', version: expoVersion }));
    await writeFile(httpSource, httpOriginal); return { app, source, core, httpSource };
}
function run(app: string, mode?: string) {
    return spawnSync(process.execPath, [command, app, ...(mode ? [mode] : [])], { encoding: 'utf8', windowsHide: true });
}

test('the pinned Expo native source is reviewed and default checking never applies a patch', async () => {
    expect(hash(original)).toBe(originalHash);
    expect(hash(httpOriginal)).toBe(httpOriginalHash);
    const value = await fixture(); const result = run(value.app);
    expect(result.status).not.toBe(0); expect(result.stderr).toContain('fix is missing');
    expect(await readFile(value.source)).toEqual(original);
    expect(await readFile(value.httpSource)).toEqual(httpOriginal);
});

test('explicit application produces the reviewed fix and repeated checks/applications preserve it', async () => {
    const value = await fixture(); expect(run(value.app, '--apply').status).toBe(0);
    const fixed = await readFile(value.source); expect(hash(fixed)).toBe(fixedHash);
    const httpFixed = await readFile(value.httpSource); expect(hash(httpFixed)).toBe(httpFixedHash);
    expect(run(value.app, '--check').status).toBe(0); expect(run(value.app, '--apply').status).toBe(0);
    expect(await readFile(value.source)).toEqual(fixed);
    expect(await readFile(value.httpSource)).toEqual(httpFixed);
});

test('an unreviewed dependency version is left untouched', async () => {
    const value = await fixture('57.0.20'); const result = run(value.app, '--apply');
    expect(result.status).not.toBe(0); expect(result.stderr).toContain('57.0.19 only');
    expect(await readFile(value.source)).toEqual(original);
});

test('locally modified dependency source is left untouched', async () => {
    const value = await fixture(); const changed = Buffer.concat([original, Buffer.from('\n// local change\n')]);
    await writeFile(value.source, changed); const result = run(value.app, '--apply');
    expect(result.status).not.toBe(0); expect(result.stderr).toContain('refusing to replace');
    expect(await readFile(value.source)).toEqual(changed);
});

test('an unreviewed Expo HTTP version leaves both dependencies untouched', async () => {
    const value = await fixture('57.0.19', '57.0.26'); const result = run(value.app, '--apply');
    expect(result.status).not.toBe(0); expect(result.stderr).toContain('expo 57.0.25 only');
    expect(await readFile(value.source)).toEqual(original); expect(await readFile(value.httpSource)).toEqual(httpOriginal);
});

test('a local HTTP change cannot partially apply the registry fix', async () => {
    const value = await fixture(); const changed = Buffer.concat([httpOriginal, Buffer.from('\n// local HTTP change\n')]);
    await writeFile(value.httpSource, changed); const result = run(value.app, '--apply');
    expect(result.status).not.toBe(0); expect(result.stderr).toContain('refusing to replace');
    expect(await readFile(value.source)).toEqual(original); expect(await readFile(value.httpSource)).toEqual(changed);
});

test('a dependency linked outside the selected app is left untouched', async () => {
    const external = await fixture();
    const app = await mkdtemp(join(tmpdir(), 'meshline-expo-compat-')); directories.push(app);
    await mkdir(join(app, 'node_modules'));
    await symlink(external.core, join(app, 'node_modules/expo-modules-core'), process.platform === 'win32' ? 'junction' : 'dir');
    const result = run(app, '--apply'); expect(result.status).not.toBe(0);
    expect(result.stderr).toContain('inside the explicitly selected app');
    expect(await readFile(external.source)).toEqual(original);
});
