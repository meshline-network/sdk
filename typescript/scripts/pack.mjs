import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const destination = fileURLToPath(new URL('../artifacts/packages/', import.meta.url));
await mkdir(destination, { recursive: true });
const npm = process.env.npm_execpath;
if (!npm) throw new Error('Run this script with npm run pack:local.');
const packages = [];
for (const entry of await readdir(new URL('../packages/', import.meta.url), { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const result = spawnSync(process.execPath,
        [npm, 'pack', '--json', '--ignore-scripts', '--workspace', `packages/${entry.name}`, '--pack-destination', destination],
        { cwd: root, encoding: 'utf8', windowsHide: true });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(result.stderr || `npm pack failed with status ${result.status}`);
    const [packed] = JSON.parse(result.stdout);
    for (const required of ['dist/index.js', 'dist/index.d.ts', 'README.md', 'LICENSE']) if (!packed.files.some(file => file.path === required)) throw new Error(`${packed.name} is missing ${required}`);
    if (packed.name === '@meshline/expo') for (const required of ['expo-module.config.json', 'android/build.gradle', 'android/src/main/AndroidManifest.xml', 'android/src/main/java/org/meshline/expo/MeshlineRelaySocketModule.kt', 'ios/MeshlineExpo.podspec', 'ios/MeshlineRelaySocketModule.swift']) if (!packed.files.some(file => file.path === required)) throw new Error(`Expo native package is missing ${required}`);
    if (packed.files.some(file => /(^|\/)(tests?|node_modules|\.git)(\/|$)/.test(file.path) || /\.tsbuildinfo$/.test(file.path))) throw new Error(`${packed.name} contains build/test-only content.`);
    if (packed.name === '@meshline/expo') for (const required of ['compatibility/apply-android-fix.mjs', 'compatibility/expo-modules-core+57.0.19.patch', 'compatibility/expo+57.0.25.patch']) if (!packed.files.some(file => file.path === required)) throw new Error(`Expo compatibility package is missing ${required}`);
    const sha256 = createHash('sha256').update(await readFile(join(destination, packed.filename))).digest('hex');
    packages.push({ name: packed.name, version: packed.version, filename: packed.filename, sha256, integrity: packed.integrity, size: packed.size, files: packed.files.length });
    console.log(`${packed.name}@${packed.version}: ${packed.filename} (${packed.files.length} files)`);
}
await writeFile(join(destination, 'manifest.json'), JSON.stringify({ node: process.version, packages }, null, 2) + '\n');
