import { mkdir, mkdtemp, readFile, realpath, writeFile, copyFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url)); const npm = process.env.npm_execpath;
if (!npm) throw new Error('Run with npm run check:packages.');
const manifestBytes = await readFile(join(root, 'artifacts/packages/manifest.json'));
const packages = JSON.parse(manifestBytes.toString('utf8')).packages;
const base = join(root, 'artifacts/consumers'); await mkdir(base, { recursive: true }); const consumer = await mkdtemp(join(base, 'node-'));
const dependencies = {};
for (const entry of packages) {
    const tarball = join(root, 'artifacts/packages', entry.filename); const hash = createHash('sha256').update(await readFile(tarball)).digest('hex');
    if (hash !== entry.sha256) throw new Error(`Package checksum differs: ${entry.name}`);
    if (entry.name !== '@meshline/expo') dependencies[entry.name] = 'file:' + relative(consumer, tarball).split(sep).join('/');
}
const workspace = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
await writeFile(join(consumer, 'package.json'), JSON.stringify({ name: 'meshline-packed-consumer', version: '0.0.0', private: true, type: 'module', dependencies, devDependencies: { '@types/node': workspace.devDependencies['@types/node'] } }, null, 2) + '\n');
function run(args) { const result = spawnSync(process.execPath, args, { cwd: consumer, encoding: 'utf8', windowsHide: true }); if (result.error) throw result.error; if (result.status !== 0) throw new Error(result.stdout + result.stderr); return result.stdout.trim(); }
// A fresh CI cache may contain locked tarballs but not registry metadata.
run([npm, 'install', '--ignore-scripts', '--no-audit', '--no-fund', '--prefer-offline']);
for (const name of Object.keys(dependencies)) {
    const installed = await realpath(join(consumer, 'node_modules', name)); if (!installed.startsWith(consumer + sep)) throw new Error(`Consumer escaped its independent installation: ${name}`);
}
await mkdir(join(consumer, 'examples'));
for (const name of ['shared.ts', 'node.ts', 'browser.ts']) await copyFile(join(root, 'examples', name), join(consumer, 'examples', name));
await writeFile(join(consumer, 'tsconfig.json'), JSON.stringify({ compilerOptions: { target: 'ES2022', module: 'NodeNext', moduleResolution: 'NodeNext', lib: ['ES2022', 'DOM', 'DOM.Iterable'], strict: true, exactOptionalPropertyTypes: true, noUncheckedIndexedAccess: true, noEmit: true, skipLibCheck: false, types: ['node'] }, include: ['examples/*.ts'] }, null, 2));
const ts = JSON.parse(await readFile(join(root, 'node_modules/typescript/package.json'), 'utf8')); run([join(root, 'node_modules/typescript', ts.bin.tsc), '-p', join(consumer, 'tsconfig.json')]);
await copyFile(join(root, 'tests/consumers/node.mjs'), join(consumer, 'smoke.mjs')); console.log(run(['smoke.mjs']));
const result = { packageManifestSha256: createHash('sha256').update(manifestBytes).digest('hex'), consumer: relative(root, consumer).split(sep).join('/'), node: process.version, packages: Object.keys(dependencies), typecheck: 'passed', nodeRuntime: 'passed', expoNativeRuntime: 'not tested', browserRuntime: 'covered separately by Playwright' };
// Native declarations must also resolve from the packed files and an independent
// install. This does not execute Hermes or load any native module.
const expoConsumer = await mkdtemp(join(base, 'expo-'));
const expoManifest = JSON.parse(await readFile(join(root, 'packages/expo/package.json'), 'utf8'));
const expoDependencies = {};
for (const name of ['@meshline/sdk', '@meshline/expo']) {
    const entry = packages.find(value => value.name === name);
    if (!entry) throw new Error(`Missing package: ${name}`);
    expoDependencies[name] = 'file:' + relative(expoConsumer, join(root, 'artifacts/packages', entry.filename)).split(sep).join('/');
}
for (const name of ['expo', 'expo-crypto', 'expo-file-system', 'expo-sqlite', 'expo-modules-core', 'react-native', 'react', '@types/react', '@types/react-native__assets-registry']) expoDependencies[name] = expoManifest.devDependencies[name];
expoDependencies['@types/node'] = workspace.devDependencies['@types/node'];
await writeFile(join(expoConsumer, 'package.json'), JSON.stringify({ name: 'meshline-packed-expo-consumer', version: '0.0.0', private: true, type: 'module', dependencies: expoDependencies }, null, 2) + '\n');
function runExpo(args) { const execution = spawnSync(process.execPath, args, { cwd: expoConsumer, encoding: 'utf8', windowsHide: true }); if (execution.error) throw execution.error; if (execution.status !== 0) throw new Error(execution.stdout + execution.stderr); return execution.stdout; }
runExpo([npm, 'install', '--ignore-scripts', '--no-audit', '--no-fund', '--prefer-offline']);
for (const name of Object.keys(expoDependencies)) {
    const installed = await realpath(join(expoConsumer, 'node_modules', name)); if (!installed.startsWith(expoConsumer + sep)) throw new Error(`Expo consumer escaped its independent installation: ${name}`);
}
await mkdir(join(expoConsumer, 'examples'));
for (const name of ['shared.ts', 'expo.ts']) await copyFile(join(root, 'examples', name), join(expoConsumer, 'examples', name));
await writeFile(join(expoConsumer, 'tsconfig.json'), JSON.stringify({ compilerOptions: { target: 'ES2022', module: 'ESNext', moduleResolution: 'Bundler', customConditions: ['react-native'], lib: ['ES2022', 'DOM', 'DOM.Iterable'], strict: true, exactOptionalPropertyTypes: true, noUncheckedIndexedAccess: true, noEmit: true, skipLibCheck: true, types: ['react-native'] }, include: ['examples/*.ts'] }, null, 2));
runExpo([join(root, 'node_modules/typescript', ts.bin.tsc), '-p', join(expoConsumer, 'tsconfig.json')]);
result.expoConsumer = relative(root, expoConsumer).split(sep).join('/'); result.expoDeclarations = 'passed';
const compatibilityCommand = join(expoConsumer, 'node_modules/@meshline/expo/compatibility/apply-android-fix.mjs');
result.expoAndroidCompatibility = JSON.parse(runExpo([compatibilityCommand, expoConsumer, '--apply']));
runExpo([compatibilityCommand, expoConsumer, '--check']);
for (const platform of ['android', 'apple']) {
    const linked = JSON.parse(runExpo([join(expoConsumer, 'node_modules/expo-modules-autolinking/bin/expo-modules-autolinking.js'), 'resolve', '--platform', platform, '--json']));
    const module = linked.modules.find(value => value.packageName === '@meshline/expo');
    if (!module || !JSON.stringify(module).includes('MeshlineRelaySocketModule')) throw new Error(`Expo ${platform} autolinking did not discover the packed native socket module.`);
}
result.expoAutolinking = 'android and apple discovered';
await writeFile(join(root, 'artifacts/packages/consumer-check.json'), JSON.stringify(result, null, 2) + '\n'); console.log(`Independent package consumer: ${result.consumer}`);
console.log(`Independent Expo declaration consumer: ${result.expoConsumer} (native runtime not tested)`);
