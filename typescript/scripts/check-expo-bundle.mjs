import { readFile, writeFile, stat } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { acceptanceSource, acceptanceSources, generatedSources, hash } from './expo-acceptance-sources.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const resultPath = join(root, 'artifacts/packages/consumer-check.json');
const result = JSON.parse(await readFile(resultPath, 'utf8'));
const manifestHash = createHash('sha256').update(await readFile(join(root, 'artifacts/packages/manifest.json'))).digest('hex');
if (manifestHash !== result.packageManifestSha256) throw new Error('Package manifest changed; rerun check:packages before compiling its Expo consumer.');
const consumer = resolve(root, result.expoConsumer);
if (!consumer.startsWith(join(root, 'artifacts', 'consumers') + sep)) throw new Error('Unexpected Expo consumer directory.');
const manifestPath = join(consumer, 'package.json');
const manifest = JSON.parse(await readFile(manifestPath, 'utf8')); manifest.main = 'index.ts';
await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
const fixtureHashes = {};
for (const name of Object.keys(acceptanceSources)) {
    const source = await acceptanceSource(root, name);
    await writeFile(join(consumer, name), source); fixtureHashes[name] = hash(source);
}
const fixtureHash = hash(JSON.stringify(fixtureHashes));
const vectorManifest = JSON.parse(await readFile(join(root, '../tests/vectors/manifest.json'), 'utf8')); const vectorFiles = {};
for (const [name, sha256] of Object.entries(vectorManifest.files)) {
    if (!/^[a-z-]+-v1\.json$/.test(name)) throw new Error('Unexpected vector filename.');
    const bytes = await readFile(join(root, '../tests/vectors', name));
    if (createHash('sha256').update(bytes).digest('hex') !== sha256) throw new Error(`Vector checksum differs: ${name}`);
    vectorFiles[name] = { source: bytes.toString('utf8'), sha256 };
}
await writeFile(join(consumer, 'vectors.ts'), `export const vectorFiles = ${JSON.stringify(vectorFiles)};\nexport const packageManifestSha256 = ${JSON.stringify(manifestHash)};\nexport const acceptanceFixtureSha256 = ${JSON.stringify(fixtureHash)};\n`);
await writeFile(join(consumer, 'index.ts'), "import { registerRootComponent } from 'expo';\nimport { createElement } from 'react';\nimport App from './App';\nimport { vectorFiles, packageManifestSha256, acceptanceFixtureSha256 } from './vectors';\nfunction AcceptanceApp() { return createElement(App, { vectors: vectorFiles, packageManifestSha256, acceptanceFixtureSha256 }); }\nregisterRootComponent(AcceptanceApp);\n");
await writeFile(join(consumer, 'app.json'), JSON.stringify({ expo: { name: 'Meshline SDK acceptance', slug: 'meshline-sdk-acceptance', version: '0.1.0', jsEngine: 'hermes', platforms: ['android', 'ios'], android: { package: 'org.meshline.sdkacceptance' }, ios: { bundleIdentifier: 'org.meshline.sdkacceptance' } } }, null, 2) + '\n');
const configPath = join(consumer, 'tsconfig.json'); const config = JSON.parse(await readFile(configPath, 'utf8')); config.compilerOptions.jsx = 'react-jsx'; config.include = [...new Set([...config.include, 'App.tsx', 'index.ts'])];
await writeFile(configPath, JSON.stringify(config, null, 2) + '\n');
async function run(args) {
    await new Promise((accept, reject) => {
        const child = spawn(process.execPath, args, { cwd: consumer, env: { ...process.env, CI: '1', EXPO_NO_TELEMETRY: '1' }, stdio: 'inherit', windowsHide: true });
        child.once('error', reject); child.once('exit', code => code === 0 ? accept() : reject(new Error(`Expo consumer command exited ${code}.`)));
    });
}
const ts = JSON.parse(await readFile(join(root, 'node_modules/typescript/package.json'), 'utf8'));
await run([join(root, 'node_modules/typescript', ts.bin.tsc), '-p', configPath]);
const bundles = [];
for (const platform of ['android', 'ios']) {
    console.log(`Compiling the installed Expo consumer for ${platform}, including Hermes bytecode.`);
    await run([join(consumer, 'node_modules/expo/bin/cli'), 'export', '--platform', platform, '--output-dir', `bundle-${platform}`, '--max-workers', '2']);
    const metadata = JSON.parse(await readFile(join(consumer, `bundle-${platform}/metadata.json`), 'utf8'));
    const bundle = metadata.fileMetadata?.[platform]?.bundle;
    if (typeof bundle !== 'string' || !bundle.endsWith('.hbc')) throw new Error(`Expo ${platform} did not generate a Hermes bytecode bundle.`);
    const artifact = join(consumer, `bundle-${platform}`, bundle); const size = (await stat(artifact)).size; if (!size) throw new Error('Empty Hermes artifact.');
    bundles.push({ platform, path: `${result.expoConsumer}/bundle-${platform}/${bundle}`, size, sha256: createHash('sha256').update(await readFile(artifact)).digest('hex') });
}
result.expoBundleArtifacts = bundles;
result.expoVectorFixtures = { sourceCommit: vectorManifest.commit, files: vectorManifest.files, nativeRuntime: 'See individual runtime receipts; new fixture not yet run' };
result.expoAcceptanceSources = {};
for (const name of [...Object.keys(acceptanceSources), ...generatedSources]) result.expoAcceptanceSources[name] = hash(await readFile(join(consumer, name)));
if (result.expoAcceptanceFixtureSha256 !== fixtureHash) {
    delete result.expoAndroidRuntimeChecks;
    result.expoNativeRuntime = 'The current acceptance fixture has not run; previous runtime receipts remain separate.';
    if (result.expoAndroidNativeBuilds?.app) result.expoAndroidNativeBuilds.app.runtime = 'Native APK unchanged; current Metro fixture has not run.';
}
result.expoAcceptanceFixtureSha256 = fixtureHash;
result.expoBundles = 'android and ios Metro/Hermes compilation passed';
await writeFile(resultPath, JSON.stringify(result, null, 2) + '\n');
console.log(result.expoBundles);
