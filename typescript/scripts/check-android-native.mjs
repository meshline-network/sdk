import { mkdir, mkdtemp, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyAcceptanceSources } from './expo-acceptance-sources.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const portable = path => relative(root, path).split(sep).join('/');
const manifestBytes = await readFile(join(root, 'artifacts/packages/manifest.json'));
const consumerCheck = JSON.parse(await readFile(join(root, 'artifacts/packages/consumer-check.json'), 'utf8'));
if (hash(manifestBytes) !== consumerCheck.packageManifestSha256) throw new Error('Package manifest changed; rerun check:packages and check:expo-bundle.');
if (!consumerCheck.expoAcceptanceSources) throw new Error('Run check:expo-bundle to prepare the native acceptance application first.');
const consumer = await realpath(resolve(root, consumerCheck.expoConsumer));
const consumers = await realpath(join(root, 'artifacts/consumers'));
if (!consumer.startsWith(consumers + sep)) throw new Error('Unexpected Expo consumer directory.');
await verifyAcceptanceSources(root, consumer, consumerCheck);
for (const entry of JSON.parse(manifestBytes.toString('utf8')).packages) {
    if (hash(await readFile(join(root, 'artifacts/packages', entry.filename))) !== entry.sha256) throw new Error(`Package checksum differs: ${entry.name}`);
}
const moduleDirectory = await realpath(join(consumer, 'node_modules/@meshline/expo'));
if (!moduleDirectory.startsWith(consumer + sep)) throw new Error('Expo adapter is not independently installed.');
const nativeSources = {};
for (const name of ['android/build.gradle', 'android/src/main/AndroidManifest.xml', 'android/src/main/java/org/meshline/expo/MeshlineRelaySocketModule.kt', 'expo-module.config.json', 'compatibility/apply-android-fix.mjs', 'compatibility/expo-modules-core+57.0.19.patch', 'compatibility/expo+57.0.25.patch']) {
    const installed = hash(await readFile(join(moduleDirectory, name)));
    if (installed !== hash(await readFile(join(root, 'packages/expo', name)))) throw new Error(`Packed native source differs from workspace: ${name}; repack and recreate the consumer.`);
    nativeSources[name] = installed;
}
if (!process.env.JAVA_HOME || !(process.env.ANDROID_HOME || process.env.ANDROID_SDK_ROOT)) throw new Error('Set JAVA_HOME and ANDROID_HOME to an installed JDK and licensed Android SDK. This check does not bootstrap these tools or accept licenses.');
const buildApp = process.argv.slice(2).includes('--app');
const testTls = process.argv.slice(2).includes('--test-tls');
if (process.argv.slice(2).some(argument => !['--app', '--test-tls'].includes(argument))) throw new Error('Only --app and --test-tls are supported.');
if (testTls && !buildApp) throw new Error('--test-tls requires --app.');
const tasks = [':meshline-expo:assembleDebug', ...(buildApp ? [':app:assembleDebug'] : [])];
const output = join(root, 'artifacts/native/android', buildApp ? 'app' : 'module');
await mkdir(output, { recursive: true });
const reportPath = join(output, 'check.json');
const report = { startedAt: new Date().toISOString(), status: 'running', host: process.platform, node: process.version, packageManifestSha256: hash(manifestBytes), consumer: portable(consumer), tasks, nativeSources, acceptanceSources: consumerCheck.expoAcceptanceSources, runtime: 'not tested', commands: [] };
report.dependencies = {};
for (const name of ['expo', 'react-native', 'expo-modules-core', '@meshline/expo']) report.dependencies[name] = JSON.parse(await readFile(join(consumer, 'node_modules', name, 'package.json'), 'utf8')).version;
const environment = { ...process.env, CI: '1', EXPO_NO_TELEMETRY: '1', GRADLE_USER_HOME: process.env.GRADLE_USER_HOME || join(root, 'artifacts/toolchains/gradle') };
async function save() { await writeFile(reportPath, JSON.stringify(report, null, 2) + '\n'); }
async function run(label, executable, args, cwd, capture = false) {
    const logPath = join(output, `${label}.log`);
    const log = createWriteStream(logPath);
    const command = { label, executable, args, log: portable(logPath), status: 'running' };
    report.commands.push(command); await save();
    let stdout = '';
    try {
        await new Promise((accept, reject) => {
            const child = spawn(executable, args, { cwd, env: environment, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
            for (const stream of [child.stdout, child.stderr]) stream.on('data', bytes => { process.stdout.write(bytes); log.write(bytes); if (capture && stream === child.stdout) stdout += bytes.toString('utf8'); });
            child.once('error', reject);
            child.once('close', (code, signal) => { command.exitCode = code; command.signal = signal; code === 0 ? accept() : reject(new Error(`${label} failed (${signal || code}); see ${portable(logPath)}.`)); });
            log.once('error', error => { child.kill(); reject(error); });
        });
        command.status = 'passed';
    } catch (error) { command.status = 'failed'; throw error; }
    finally { await new Promise(accept => log.end(accept)); await save(); }
    return stdout;
}
await save();
try {
    report.androidCompatibility = JSON.parse(await run('android-compatibility', process.execPath, [join(moduleDirectory, 'compatibility/apply-android-fix.mjs'), consumer, '--check'], consumer, true));
    await run('java', join(process.env.JAVA_HOME, 'bin', process.platform === 'win32' ? 'java.exe' : 'java'), ['-version'], consumer);
    await run('prebuild', process.execPath, [join(consumer, 'node_modules/expo/bin/cli'), 'prebuild', '--platform', 'android', '--no-install'], consumer);
    const android = join(consumer, 'android');
    const debug = join(android, 'app/src/debug');
    const debugManifest = join(debug, 'AndroidManifest.xml');
    const debugXml = await readFile(debugManifest, 'utf8');
    // Remove only the attribute managed by this fixture before reapplying it.
    // A later build without --test-tls must not retain an active local CA.
    const cleanDebugXml = debugXml.replace(' android:networkSecurityConfig="@xml/meshline_test_network"', '');
    if (cleanDebugXml !== debugXml) await writeFile(debugManifest, cleanDebugXml);
    if (testTls) {
        const ca = await readFile(join(root, 'artifacts/native/tls/ca.pem'));
        await mkdir(join(debug, 'res/raw'), { recursive: true }); await mkdir(join(debug, 'res/xml'), { recursive: true });
        await writeFile(join(debug, 'res/raw/meshline_test_ca.pem'), ca);
        await writeFile(join(debug, 'res/xml/meshline_test_network.xml'), '<network-security-config><base-config cleartextTrafficPermitted="true"><trust-anchors><certificates src="system" /></trust-anchors></base-config><domain-config><domain>127.0.0.1</domain><trust-anchors><certificates src="@raw/meshline_test_ca" /></trust-anchors></domain-config></network-security-config>');
        if (cleanDebugXml.includes('android:networkSecurityConfig')) throw new Error('Unexpected pre-existing test application network security configuration.');
        if (!cleanDebugXml.includes('<application ')) throw new Error('Missing debug application manifest element.');
        await writeFile(debugManifest, cleanDebugXml.replace('<application ', '<application android:networkSecurityConfig="@xml/meshline_test_network" '));
        report.testTls = { caSha256: hash(ca), scope: 'debug acceptance application, 127.0.0.1 only' }; await save();
    }
    const gradleArgs = [...tasks, '--no-daemon', '--console=plain', '--stacktrace', '--max-workers=2', '-PreactNativeArchitectures=x86_64', '-Pandroid.builder.sdkDownload=false'];
    // All shell arguments are fixed here; do not interpolate user paths into cmd.exe.
    await run('gradle', process.platform === 'win32' ? 'cmd.exe' : './gradlew', process.platform === 'win32' ? ['/d', '/s', '/c', `gradlew.bat ${gradleArgs.join(' ')}`] : gradleArgs, android);
    report.artifacts = [];
    for (const path of [join(moduleDirectory, 'android/build/outputs/aar/meshline-expo-debug.aar'), ...(buildApp ? [join(android, 'app/build/outputs/apk/debug/app-debug.apk')] : [])]) {
        const size = (await stat(path)).size;
        if (!size) throw new Error(`Empty native artifact: ${path}`);
        report.artifacts.push({ path: portable(path), size, sha256: hash(await readFile(path)) });
    }
    const inspection = await mkdtemp(join(output, 'classes-'));
    const jar = join(process.env.JAVA_HOME, 'bin', process.platform === 'win32' ? 'jar.exe' : 'jar');
    await run('aar', jar, ['--extract', '--file', join(root, report.artifacts[0].path), 'classes.jar'], inspection);
    const entries = await run('classes', jar, ['--list', '--file', 'classes.jar'], inspection, true);
    const moduleClass = 'org/meshline/expo/MeshlineRelaySocketModule.class';
    if (!entries.split(/\r?\n/).includes(moduleClass)) throw new Error('Native AAR does not contain the autolinked MeshlineRelaySocketModule class.');
    report.nativeModuleClass = moduleClass;
    report.wrapper = { sha256: hash(await readFile(join(android, 'gradle/wrapper/gradle-wrapper.properties'))) };
    report.status = 'passed';
    const currentConsumer = JSON.parse(await readFile(join(root, 'artifacts/packages/consumer-check.json'), 'utf8'));
    if (currentConsumer.packageManifestSha256 !== report.packageManifestSha256 || currentConsumer.expoConsumer !== report.consumer) throw new Error('Package consumer changed during the native build; do not attach this result to a different installation.');
    currentConsumer.expoAndroidNativeBuilds = { ...currentConsumer.expoAndroidNativeBuilds, [buildApp ? 'app' : 'module']: { status: 'passed', report: portable(reportPath), runtime: 'not tested' } };
    await writeFile(join(root, 'artifacts/packages/consumer-check.json'), JSON.stringify(currentConsumer, null, 2) + '\n');
    console.log('Android native compilation passed; device/Hermes runtime remains untested.');
} catch (error) { report.status = 'failed'; report.error = String(error); throw error; }
finally { report.finishedAt = new Date().toISOString(); await save(); }
