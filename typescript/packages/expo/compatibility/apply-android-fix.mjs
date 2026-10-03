#!/usr/bin/env node
import { readFile, realpath, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';

const [appDirectory, mode = '--check'] = process.argv.slice(2);
if (!appDirectory || !['--check', '--apply'].includes(mode) || process.argv.length > 4) throw new Error('Usage: meshline-expo-android-compat <app-directory> [--check|--apply]');
const app = await realpath(resolve(appDirectory));
const digest = value => createHash('sha256').update(value).digest('hex');
const fixes = [{
    name: 'expo-modules-core', version: '57.0.19', path: 'android/src/main/java/expo/modules/kotlin/sharedobjects/SharedObjectRegistry.kt',
    originalSha256: 'c41c2a8aab60bef0476b2f805aa863507b96144701d782a228e76c1afbf1b4b3',
    fixedSha256: 'b9417c719e9cdd92c1f08ba8d841e4b9cbfd933dd031b7b2e3a8ff44b9861df4',
    apply: source => source.replace(
        '    val native = pairs[id.ensureWasNotRelease()]?.first\n    return native ?: throw InvalidSharedObjectIdException()',
        '    return synchronized(this) {\n      val native = pairs[id.ensureWasNotRelease()]?.first\n      native ?: throw InvalidSharedObjectIdException()\n    }',
    ).replace(
        '    return pairs[id]?.first',
        '    return synchronized(this) {\n      pairs[id]?.first\n    }',
    ),
}, {
    name: 'expo', version: '57.0.25', path: 'android/src/main/java/expo/modules/fetch/NativeRequest.kt',
    originalSha256: '4e35ef082ef083490e7088d732af787c348678bc653deab3ef646ef9c1707938',
    fixedSha256: '6fc9d773e12eb1788cde5392c666e495131240b2240657c85664de02aa3fc458',
    apply: source => source.replace('    this.task?.enqueue(this.response)\n    response.onStarted()',
        '    response.onStarted()\n    this.task?.enqueue(this.response)'),
}];
// Validate every target before writing either dependency. A rejected second
// dependency must not leave the first dependency partially patched.
const changes = [];
for (const fix of fixes) {
    const dependency = await realpath(join(app, 'node_modules', fix.name));
    const withinApp = relative(app, dependency);
    if (!withinApp || isAbsolute(withinApp) || withinApp.startsWith('..' + sep) || withinApp === '..') throw new Error('The dependency must be installed inside the explicitly selected app directory.');
    const metadata = JSON.parse(await readFile(join(dependency, 'package.json'), 'utf8'));
    if (metadata.name !== fix.name || metadata.version !== fix.version) throw new Error(`This reviewed compatibility fix targets ${fix.name} ${fix.version} only; review other versions separately.`);
    const sourcePath = join(dependency, fix.path); const source = await readFile(sourcePath, 'utf8'); const current = digest(source);
    if (current !== fix.originalSha256 && current !== fix.fixedSha256) throw new Error(`${fix.name} native source differs from both reviewed versions; refusing to replace it.`);
    if (current === fix.originalSha256 && mode !== '--apply') throw new Error(`${fix.name} Android compatibility fix is missing. Apply the reviewed patch and rebuild the native app.`);
    const updated = current === fix.fixedSha256 ? source : fix.apply(source);
    if (digest(updated) !== fix.fixedSha256) throw new Error('Patched source fingerprint differs from the reviewed fix.');
    changes.push({ fix, sourcePath, current, updated });
}
for (const change of changes) if (change.current !== change.fix.fixedSha256) await writeFile(change.sourcePath, change.updated);
const receipt = change => ({ version: change.fix.version, status: change.current === change.fix.fixedSha256 ? 'already applied' : 'applied', source: change.sourcePath, sourceSha256: change.fix.fixedSha256, nativeRebuildRequired: true });
console.log(JSON.stringify({ ...receipt(changes[0]), http: receipt(changes[1]) }));
