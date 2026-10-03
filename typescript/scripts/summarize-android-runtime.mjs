import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';
import { verifyAcceptanceSources } from './expo-acceptance-sources.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const manifestHash = hash(await readFile(join(root, 'artifacts/packages/manifest.json')));
const consumer = JSON.parse(await readFile(join(root, 'artifacts/packages/consumer-check.json'), 'utf8'));
const build = JSON.parse(await readFile(join(root, 'artifacts/native/android/app/check.json'), 'utf8'));
if (build.androidCompatibility?.http?.version !== '57.0.25' || build.androidCompatibility.http.sourceSha256 !== '6fc9d773e12eb1788cde5392c666e495131240b2240657c85664de02aa3fc458') throw new Error('Native build must include the reviewed Expo HTTP start-order fix.');
if (build.androidCompatibility?.version !== '57.0.19' || build.androidCompatibility.sourceSha256 !== 'b9417c719e9cdd92c1f08ba8d841e4b9cbfd933dd031b7b2e3a8ff44b9861df4') throw new Error('The native build is missing the reviewed Expo registry fix.');
if (consumer.packageManifestSha256 !== manifestHash || build.packageManifestSha256 !== manifestHash
    || build.consumer !== consumer.expoConsumer || build.status !== 'passed') throw new Error('Current package, consumer and native build do not match.');
await verifyAcceptanceSources(root, resolve(root, consumer.expoConsumer), consumer);
const apk = build.artifacts.find(value => value.path.endsWith('.apk'));
if (!apk) throw new Error('Missing compiled APK receipt.');
const directory = join(root, 'artifacts/native/android/runtime');
const expected = { local: 222, transport: 18, restart: 6, workflows: 16, lifecycle: 12, reconnect: 14, interop: 27, recovery: 25 };
const checks = []; let serial; let device;
for (const [kind, count] of Object.entries(expected)) {
    const bytes = await readFile(join(directory, kind, 'check.json'));
    const receipt = JSON.parse(bytes); const resultBytes = await readFile(join(directory, kind, 'result.json'));
    const result = JSON.parse(resultBytes);
    const started = Date.parse(result.startedAt); const finished = Date.parse(result.finishedAt); const built = Date.parse(build.finishedAt);
    if (receipt.kind !== kind || receipt.status !== 'passed' || receipt.passed !== count || receipt.failed !== 0
        || receipt.packageManifestSha256 !== manifestHash || receipt.installedApkSha256 !== apk.sha256
        || receipt.consumer !== consumer.expoConsumer || receipt.acceptanceFixtureSha256 !== consumer.expoAcceptanceFixtureSha256
        || receipt.androidCompatibility?.sourceSha256 !== build.androidCompatibility?.sourceSha256
        || receipt.androidCompatibility?.http?.sourceSha256 !== build.androidCompatibility.http.sourceSha256
        || hash(resultBytes) !== receipt.runtimeReportSha256 || result.scope !== kind || result.passed !== count || result.failed !== 0
        || result.packageManifestSha256 !== manifestHash || result.acceptanceFixtureSha256 !== consumer.expoAcceptanceFixtureSha256
        || result.results.length !== count || result.results.some(value => value.passed !== true)
        || !Number.isFinite(started) || !Number.isFinite(finished) || !Number.isFinite(built)
        || started < built || finished < started) throw new Error(`Stale, incomplete or failed Android receipt: ${kind}`);
    if (serial !== undefined && (serial !== receipt.serial || JSON.stringify(device) !== JSON.stringify(receipt.device))) throw new Error('Android subsets came from different devices.');
    serial = receipt.serial; device = receipt.device;
    checks.push({ kind, passed: count, failed: 0, report: `${kind}/check.json`, sha256: hash(bytes), acceptanceFixtureSha256: receipt.acceptanceFixtureSha256 });
}
let doze;
try {
    const bytes = await readFile(join(directory, 'lifecycle/doze.json')); const receipt = JSON.parse(bytes);
    const lifecycle = checks.find(value => value.kind === 'lifecycle');
    if (receipt.status !== 'passed' || receipt.packageManifestSha256 !== manifestHash
        || receipt.acceptanceFixtureSha256 !== consumer.expoAcceptanceFixtureSha256
        || receipt.lifecycleCheckSha256 !== lifecycle.sha256 || !/^\d+$/.test(receipt.processId)
        || receipt.observations.length !== 2 || receipt.observations.some(value => value.deep !== 'IDLE'
            || value.processId !== receipt.processId || value.appState !== 'background' || value.client !== 'stopped'
            || !Number.isFinite(Date.parse(value.at)))
        || Date.parse(receipt.observations[1].at) - Date.parse(receipt.observations[0].at) < 20000
        || receipt.restored.deep !== 'ACTIVE' || receipt.restored.deepEnabled !== receipt.originalDeepEnabled
        || receipt.restored.processId !== receipt.processId) throw new Error('Stale or incomplete controlled Doze receipt.');
    doze = { report: 'lifecycle/doze.json', sha256: hash(bytes), scope: receipt.scope };
} catch (error) { if (error?.code !== 'ENOENT') throw error; }
const summary = { verifiedAt: new Date().toISOString(), packageManifestSha256: manifestHash, installedApkSha256: apk.sha256,
    acceptanceFixtureSha256: consumer.expoAcceptanceFixtureSha256, androidCompatibility: build.androidCompatibility, serial, device, runtime: 'Hermes', checks,
    totalPassed: checks.reduce((sum, value) => sum + value.passed, 0), totalFailed: 0,
    androidDeliveryAcceptance: doze ? 'passed' : 'controlled Doze still required', fullNativeAcceptance: 'iOS deferred by user',
    ...(doze ? { controlledDoze: doze } : {}),
    deferredByUser: ['iOS native compile and runtime', 'Linux runtime'],
    limitations: ['Natural/OEM suspension and background delivery beyond controlled Doze',
        'Unenumerated concurrent failures and interruption points outside the named fixtures',
        'Deployed production relay acceptance', 'Windows WebKit cookie omission failure'] };
await writeFile(join(directory, 'summary.json'), JSON.stringify(summary, null, 2) + '\n');
consumer.expoNativeRuntime = `Android: ${summary.totalPassed} checks passed; ${summary.androidDeliveryAcceptance}; iOS deferred by user`;
if (consumer.expoAndroidNativeBuilds?.app) consumer.expoAndroidNativeBuilds.app.runtime = consumer.expoNativeRuntime;
await writeFile(join(root, 'artifacts/packages/consumer-check.json'), JSON.stringify(consumer, null, 2) + '\n');
console.log(`Android named subsets: ${summary.totalPassed} passed, 0 failed. Delivery acceptance: ${summary.androidDeliveryAcceptance}; Linux/iOS deferred.`);
