import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyAcceptanceSources } from './expo-acceptance-sources.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const [serial, kind = 'local'] = process.argv.slice(2);
if (!serial || !/^[\w.:-]+$/.test(serial) || !['local', 'transport', 'prepare-restart', 'restart', 'workflows', 'lifecycle', 'reconnect', 'interop', 'prepare-recovery', 'recovery'].includes(kind) || process.argv.length > 4) throw new Error('Usage: node scripts/collect-android-runtime.mjs <adb-serial> [local|transport|prepare-restart|restart|workflows|lifecycle|reconnect|interop|prepare-recovery|recovery]');
const sdk = process.env.ANDROID_HOME || process.env.ANDROID_SDK_ROOT;
if (!sdk) throw new Error('Set ANDROID_HOME to the installed SDK.');
const adb = join(sdk, 'platform-tools', process.platform === 'win32' ? 'adb.exe' : 'adb');
const exec = promisify(execFile);
async function command(...args) { return (await exec(adb, ['-s', serial, ...args], { windowsHide: true, maxBuffer: 1024 * 1024, timeout: 15000 })).stdout.trim(); }
const hash = value => createHash('sha256').update(value).digest('hex');
const manifest = await readFile(join(root, 'artifacts/packages/manifest.json'));
const consumerPath = join(root, 'artifacts/packages/consumer-check.json');
const consumer = JSON.parse(await readFile(consumerPath, 'utf8'));
const build = JSON.parse(await readFile(join(root, 'artifacts/native/android/app/check.json'), 'utf8'));
if (build.androidCompatibility?.http?.version !== '57.0.25' || build.androidCompatibility.http.sourceSha256 !== '6fc9d773e12eb1788cde5392c666e495131240b2240657c85664de02aa3fc458') throw new Error('Native build must include the reviewed Expo HTTP start-order fix.');
if (build.androidCompatibility?.version !== '57.0.19' || build.androidCompatibility.sourceSha256 !== 'b9417c719e9cdd92c1f08ba8d841e4b9cbfd933dd031b7b2e3a8ff44b9861df4') throw new Error('Native build must include the reviewed Expo Android registry fix.');
if (build.status !== 'passed' || build.packageManifestSha256 !== hash(manifest) || consumer.packageManifestSha256 !== hash(manifest) || build.consumer !== consumer.expoConsumer) throw new Error('Current package, consumer and successful native build must match.');
const consumerDirectory = resolve(root, consumer.expoConsumer);
if (!consumerDirectory.startsWith(join(root, 'artifacts/consumers') + sep)) throw new Error('Unexpected consumer path.');
// This debug APK loads JavaScript from Metro. Bind JS to its current independent
// bundle receipt, and native code to the installed APK/build receipt separately.
await verifyAcceptanceSources(root, consumerDirectory, consumer);
if (await command('get-state') !== 'device') throw new Error('Device is not online.');
const installed = (await command('shell', 'pm', 'path', 'org.meshline.sdkacceptance')).replace(/^package:/, '');
if (!/^\/data\/app\/[\w/+.=~-]+\.apk$/.test(installed)) throw new Error('Unexpected installed acceptance APK path.');
const installedSha256 = (await command('shell', 'sha256sum', installed)).split(/\s+/)[0];
const apk = build.artifacts.find(value => value.path.endsWith('.apk'));
if (!apk || installedSha256 !== apk.sha256) throw new Error('Installed APK does not match the verified native build.');
const reportName = kind === 'local' ? 'meshline-native-acceptance.json' : `meshline-native-${kind}.json`;
const text = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', `files/${reportName}`);
const result = JSON.parse(text);
if (result.packageManifestSha256 !== hash(manifest) || result.platform !== 'android' || result.hermes !== true) throw new Error('Runtime report does not identify this Android/Hermes package.');
if (result.scope !== kind || result.acceptanceFixtureSha256 !== consumer.expoAcceptanceFixtureSha256) throw new Error('Runtime report was produced by a different acceptance fixture or mode.');
const started = Date.parse(result.startedAt); const finished = Date.parse(result.finishedAt);
if (!Number.isFinite(started) || !Number.isFinite(finished) || started < Date.parse(build.finishedAt) || finished < started) throw new Error('Stale or invalid runtime timestamp.');
const expected = { local: 222, transport: 18, 'prepare-restart': 1, restart: 6, workflows: 16, lifecycle: 12, reconnect: 14, interop: 27, 'prepare-recovery': 11, recovery: 25 }[kind];
const passed = result.results.filter(row => row.passed === true).length; const failed = result.results.filter(row => row.passed === false).length;
if (result.results.length !== expected || result.passed !== passed || result.failed !== failed || passed + failed !== expected) throw new Error('Incomplete or inconsistent per-check runtime report.');
const directory = join(root, 'artifacts/native/android/runtime', kind); await mkdir(directory, { recursive: true });
await writeFile(join(directory, 'result.json'), text + '\n');
const receipt = { collectedAt: new Date().toISOString(), status: failed ? 'failed' : 'passed', serial, kind, passed, failed,
    packageManifestSha256: hash(manifest), installedApkSha256: installedSha256, consumer: consumer.expoConsumer,
    acceptanceSources: consumer.expoAcceptanceSources, acceptanceFixtureSha256: consumer.expoAcceptanceFixtureSha256, dependencies: build.dependencies, testTls: build.testTls, androidCompatibility: build.androidCompatibility,
    device: { release: await command('shell', 'getprop', 'ro.build.version.release'), api: await command('shell', 'getprop', 'ro.build.version.sdk'), abi: await command('shell', 'getprop', 'ro.product.cpu.abi') },
    runtimeReportSha256: hash(text + '\n'), fullNativeAcceptance: 'not complete' };
if (kind === 'prepare-restart' || kind === 'restart') {
    receipt.processId = await command('shell', 'pidof', 'org.meshline.sdkacceptance');
    if (!/^\d+$/.test(receipt.processId)) throw new Error('Expected exactly one acceptance app process.');
    receipt.restartStateSha256 = hash(await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', 'files/meshline-restart-state.json'));
    if (kind === 'restart') {
        const prepared = JSON.parse(await readFile(join(root, 'artifacts/native/android/runtime/prepare-restart/check.json'), 'utf8'));
        if (prepared.status !== 'passed' || prepared.serial !== serial || prepared.packageManifestSha256 !== receipt.packageManifestSha256 || prepared.processId === receipt.processId || prepared.restartStateSha256 !== receipt.restartStateSha256 || started < Date.parse(prepared.collectedAt)) throw new Error('A verified preparation in a different native process is required.');
        receipt.previousProcessId = prepared.processId;
    }
}
if (kind === 'lifecycle') {
    const eventsText = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', 'files/meshline-native-lifecycle-events.json');
    const events = JSON.parse(eventsText);
    if (events.map(value => value.phase).join(',') !== 'ready-for-background,background-stopped,foreground-running,completed'
        || events.some(value => !Number.isFinite(Date.parse(value.at)) || Date.parse(value.at) < started || Date.parse(value.at) > finished)
        || events[1].appState !== 'background' || events[1].client !== 'stopped' || events[1].children.length !== 6 || events[1].children.some(value => value !== 'stopped')
        || events[2].appState !== 'active' || events[2].client !== 'running' || events[2].children.length !== 6 || events[2].children.some(value => value !== 'running')) throw new Error('Native background/resume state evidence is incomplete.');
    await writeFile(join(directory, 'events.json'), eventsText + '\n'); receipt.lifecycleEventsSha256 = hash(eventsText + '\n');
}
if (kind === 'reconnect') {
    const evidenceText = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', 'files/meshline-native-reconnect-evidence.json');
    const evidence = JSON.parse(evidenceText);
    const peerReceipt = JSON.parse(await readFile(join(root, 'artifacts/native/tls/peer.json'), 'utf8'));
    const hostRuns = JSON.parse(await readFile(join(root, 'artifacts/native/tls/reconnect-observations.json'), 'utf8'));
    const host = hostRuns[evidence.run];
    if (peerReceipt.reconnectPeerSha256 !== hash(await readFile(join(root, 'tests/support/reconnect-peer.ts')))
        || peerReceipt.caSha256 !== build.testTls?.caSha256 || !Number.isFinite(Date.parse(peerReceipt.startedAt))) throw new Error('Reconnect peer source, CA or start time differs from this test.');
    // The emulator and host have independent wall clocks. Bind the app to the
    // exact host snapshot by its random run ID; validate server timestamps only
    // against the host's peer start and collection times, never device time.
    if (!host || JSON.stringify(host) !== JSON.stringify(evidence.server) || host.errors.length || host.openConnections.length
        || host.events.filter(value => value.kind === 'open').length !== 4 || host.events.filter(value => value.kind === 'close').length !== 4
        || host.events.some((value, index) => value.index !== index || !Number.isFinite(value.at)
            || value.at < Date.parse(peerReceipt.startedAt) || value.at > Date.parse(receipt.collectedAt))) throw new Error('Independent reconnect server observations differ or are incomplete.');
    await writeFile(join(directory, 'evidence.json'), evidenceText + '\n');
    await writeFile(join(directory, 'server-observations.json'), JSON.stringify(host, null, 2) + '\n');
    receipt.reconnectEvidenceSha256 = hash(evidenceText + '\n'); receipt.reconnectPeerSha256 = peerReceipt.reconnectPeerSha256;
    receipt.reconnectClockValidation = { server: 'host peer start through collection', app: 'device start through finish', binding: 'random run ID and identical complete server snapshot' };
}
if (['interop', 'prepare-recovery', 'recovery'].includes(kind)) {
    const evidenceText = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', `files/meshline-native-${kind}-evidence.json`);
    const evidence = JSON.parse(evidenceText);
    if (!/^[a-f0-9]{24}$/.test(evidence.run)) throw new Error('Invalid interop run identifier.');
    const diagnosticsText = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', `files/meshline-native-${kind}-diagnostics.json`);
    const diagnostics = JSON.parse(diagnosticsText);
    if (!Array.isArray(diagnostics) || diagnostics.length) throw new Error('Native interop background diagnostics failed.');
    if (kind === 'interop') {
        const waitsText = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', 'files/meshline-native-interop-waits.json');
        const completed = JSON.parse(waitsText).filter(value => value.complete);
        if (completed.map(value => value.phase).join(',') !== 'native-history,dotnet-history'
            || completed.some(value => !Number.isFinite(value.elapsedMs) || value.elapsedMs < 0 || value.elapsedMs > 90000)) throw new Error('Native interop convergence evidence failed.');
        await writeFile(join(directory, 'waits.json'), waitsText + '\n'); receipt.interopWaitsSha256 = hash(waitsText + '\n');
    }
    const peerReceipt = JSON.parse(await readFile(join(root, 'artifacts/native/interop/peer.json'), 'utf8'));
    const host = JSON.parse(await readFile(join(root, 'artifacts/native/interop/observations.json'), 'utf8'))[evidence.run];
    if (peerReceipt.caSha256 !== build.testTls?.caSha256 || Date.parse(peerReceipt.startedAt) > started
        || peerReceipt.bundleSha256 !== hash(await readFile(join(root, 'artifacts/native/interop/peer.mjs')))) throw new Error('Interop peer CA, bundle or start time differs.');
    for (const [path, sha256] of Object.entries(peerReceipt.sources)) if (sha256 !== hash(await readFile(join(root, path)))) throw new Error(`Interop peer source or .NET binary differs: ${path}`);
    if (!host || !Number.isInteger(host.processId) || host.errors.length || evidence.errors.length) throw new Error('Independent .NET peer observations failed.');
    if (kind === 'prepare-recovery') {
        if (host.closed || host.databases.length || JSON.stringify(host.faults) !== JSON.stringify(evidence.faults)
            || JSON.stringify(host.requests.slice(0, evidence.requests.length)) !== JSON.stringify(evidence.requests)) throw new Error('Pending recovery observations differ.');
    } else if (JSON.stringify(host) !== JSON.stringify(evidence) || !host.closed || host.databases.length !== (kind === 'interop' ? 7 : 1)) throw new Error('Closed .NET peer observations differ or are incomplete.');
    for (const database of host.databases) {
        if (!/^[0-9]+-[a-z0-9-]+\.sqlite$/.test(database.name) || database.size <= 0 || database.tables.Groups < 1 || database.tables.GroupEvents < 1 || database.tables.GroupMemberKeys < 1
            || database.sha256 !== hash(await readFile(join(root, 'artifacts/native/interop/runs', evidence.run, database.name)))) throw new Error('The .NET SQLite database receipt differs.');
        if (database.wal && database.wal.sha256 !== hash(await readFile(join(root, 'artifacts/native/interop/runs', evidence.run, database.name + '-wal')))) throw new Error('The .NET SQLite WAL receipt differs.');
    }
    if (kind === 'interop') for (const actor of ['typescript', 'dotnet']) for (const method of ['group.application.approve', 'group.secret.rotation.commit', 'group.member.recovery.approve', 'message.send']) {
        if (!host.requests.some(value => value.actor === actor && value.method === method && value.status >= 200 && value.status < 300)) throw new Error(`Missing successful ${actor} request: ${method}`);
    }
    if (kind !== 'interop') {
        const stateText = await command('shell', 'run-as', 'org.meshline.sdkacceptance', 'cat', 'files/meshline-process-recovery-state.json'); const state = JSON.parse(stateText);
        receipt.processId = await command('shell', 'pidof', 'org.meshline.sdkacceptance');
        if (!/^\d+$/.test(receipt.processId) || state.packageManifestSha256 !== receipt.packageManifestSha256
            || state.acceptanceFixtureSha256 !== receipt.acceptanceFixtureSha256 || state.state.fixture.run !== evidence.run
            || state.state.persisted.length !== 7 || !/^chan_[A-Za-z0-9_-]{22}$/.test(state.state.channel?.channelId)
            || state.state.database !== `meshline-process-${evidence.run}.sqlite` || state.state.members?.length !== 2
            || state.state.members.some((member, index) => member.kind !== ['application', 'recovery'][index] || member.seed !== 119 + index
                || member.database !== `meshline-process-${evidence.run}-${member.kind}.sqlite` || member.persisted.length !== 3
                || !/^grp_[A-Za-z0-9_-]{22}$/.test(member.group?.groupId) || member.group.relayId !== state.state.fixture.relayId
                || typeof member.accountId !== 'string' || !/^[A-Za-z0-9_-]{43}$/.test(member.candidatePublicKey) || !/^[a-f0-9]{64}$/.test(member.protectedKeySha256))
            || !/^msg_[A-Za-z0-9_-]{22}$/.test(state.state.channelMessageId) || !Number.isSafeInteger(state.state.channelSequence) || state.state.channelSequence < 1) throw new Error('Recovery state does not match this process, fixture or run.');
        const methods = host.faults.map(value => value.method).sort().join(',');
        if (methods !== 'channel.post,group.application.submit,group.member.recovery.submit,group.secret.rotation.commit,message.send' || host.faults.some(value => !Number.isFinite(value.acceptedAt)
            || value.acceptedAt < Date.parse(peerReceipt.startedAt) || value.acceptedAt > Date.parse(receipt.collectedAt)
            || (kind === 'prepare-recovery' ? value.disconnectedAt !== undefined : !Number.isFinite(value.disconnectedAt) || value.disconnectedAt < value.acceptedAt))) throw new Error('External process interruption was not observed for all five accepted requests.');
        for (const member of state.state.members) {
            const fault = host.faults.find(value => value.method === (member.kind === 'application' ? 'group.application.submit' : 'group.member.recovery.submit'));
            if (fault.resourceId !== member.group.groupId || fault.account !== member.accountId
                || host.requests.filter(value => value.actor === 'typescript' && value.method === fault.method && value.bodySha256 === fault.bodySha256).length !== 1) throw new Error('Original member submission changed or was repeated.');
        }
        if (!Array.isArray(host.offlineApprovals) || (kind === 'prepare-recovery' && (host.offlineApprovals.length || evidence.offlineApprovals.length))) throw new Error('Owner approval happened before process termination.');
        receipt.recoveryStateSha256 = hash(stateText); await writeFile(join(directory, 'state.json'), stateText);
        if (kind === 'recovery') {
            const prepared = JSON.parse(await readFile(join(root, 'artifacts/native/android/runtime/prepare-recovery/check.json'), 'utf8'));
            const original = JSON.parse(await readFile(join(root, 'artifacts/native/android/runtime/prepare-recovery/result.json'), 'utf8'));
            if (prepared.status !== 'passed' || prepared.serial !== serial || prepared.acceptanceFixtureSha256 !== receipt.acceptanceFixtureSha256
                || prepared.installedApkSha256 !== receipt.installedApkSha256 || prepared.processId === receipt.processId
                || prepared.recoveryStateSha256 !== receipt.recoveryStateSha256 || prepared.dotnetProcessId !== host.processId
                || started <= Date.parse(original.finishedAt)) throw new Error('Verified preparation in a different process with unchanged state is required.');
            const direct = host.faults.find(value => value.method === 'message.send');
            const publication = host.faults.find(value => value.method === 'channel.post');
            if (host.requests.filter(value => value.actor === 'typescript' && value.method === 'message.send' && value.bodySha256 === direct.bodySha256).length !== 2
                || host.requests.filter(value => value.actor === 'typescript' && value.method === 'group.secret.rotation.commit').length !== 1
                || host.requests.filter(value => value.actor === 'typescript' && value.method === 'channel.post' && value.bodySha256 === publication.bodySha256).length !== 1
                || host.databases[0].tables.Groups !== 3 || host.databases[0].tables.Channels !== 1 || host.databases[0].tables.ChannelPosts !== 3) throw new Error('Exact message retry, group/publication reconciliation or retained history differs.');
            const preparationDirectory = join(root, 'artifacts/native/android/runtime/prepare-recovery');
            const terminationBytes = await readFile(join(preparationDirectory, 'termination.json')); const termination = JSON.parse(terminationBytes);
            const inspectionBytes = await readFile(join(preparationDirectory, 'native-storage-inspection.json')); const inspection = JSON.parse(inspectionBytes);
            const approvalBytes = await readFile(join(preparationDirectory, 'offline-approvals.json')); const approval = JSON.parse(approvalBytes);
            if (approval.status !== 'passed' || approval.run !== evidence.run || approval.serial !== serial || approval.previousProcessId !== prepared.processId
                || approval.processAbsentBefore !== true || approval.processAbsentAfter !== true
                || approval.packageManifestSha256 !== receipt.packageManifestSha256 || approval.acceptanceFixtureSha256 !== receipt.acceptanceFixtureSha256
                || approval.recoveryStateSha256 !== receipt.recoveryStateSha256 || approval.preparationCheckSha256 !== hash(await readFile(join(preparationDirectory, 'check.json')))
                || approval.terminationSha256 !== hash(terminationBytes) || approval.inspectionSha256 !== hash(inspectionBytes)
                || !Number.isFinite(Date.parse(approval.absentBeforeAt)) || !Number.isFinite(Date.parse(approval.absentAfterAt))
                || Date.parse(approval.absentBeforeAt) < Date.parse(termination.at) || Date.parse(approval.absentAfterAt) < Date.parse(approval.absentBeforeAt)
                || Date.parse(approval.absentAfterAt) > Date.parse(receipt.collectedAt)
                || termination.processAbsent !== true || termination.originalProcessId !== prepared.processId || termination.serial !== serial
                || termination.preparationCheckSha256 !== approval.preparationCheckSha256 || termination.recoveryStateSha256 !== receipt.recoveryStateSha256
                || inspection.terminationSha256 !== hash(terminationBytes) || inspection.integrity !== 'ok' || inspection.durableFileHashesMatch !== true
                || inspection.databases.length !== 3 || inspection.rows.length !== 13 || termination.files.length !== 9
                || JSON.stringify(approval.approvals) !== JSON.stringify(host.offlineApprovals) || host.offlineApprovals.length !== 2) throw new Error('Independent stopped-process storage or offline approval evidence differs.');
            for (const store of [state.state, ...state.state.members]) {
                if (!inspection.databases.some(value => value.name === store.database && value.integrity === 'ok' && value.matchedRecords === store.persisted.length)
                    || store.persisted.some(item => !inspection.rows.some(row => row.database === store.database && row.matches === true && row.sha256 === item.sha256
                        && row.query.collection === item.query.collection && row.query.key === item.query.key))) throw new Error('Independent native storage fingerprints differ.');
            }
            for (const file of termination.files) {
                if (!/^(main|application|recovery)-before-recovery\.sqlite(-wal|-shm)?$/.test(file.file)) throw new Error('Unexpected captured native file.');
                if (!file.file.endsWith('-shm') && hash(await readFile(join(preparationDirectory, file.file))) !== file.sha256) throw new Error('Captured native SQLite or WAL changed.');
            }
            for (const member of state.state.members) {
                const method = member.kind === 'application' ? 'group.application.approve' : 'group.member.recovery.approve';
                if (!host.offlineApprovals.some(value => value.method === method && value.groupId === member.group.groupId && value.account === member.accountId
                    && value.memberPublicKey === member.candidatePublicKey && /^msg_[A-Za-z0-9_-]{22}$/.test(value.welcomeMessageId)
                    && value.approvedAt >= Math.max(...host.faults.map(fault => fault.disconnectedAt))
                    && value.approvedAt >= Date.parse(approval.absentBeforeAt) && value.approvedAt <= Date.parse(approval.absentAfterAt))
                    || !host.requests.some(value => value.actor === 'dotnet' && value.method === method && value.status >= 200 && value.status < 300)) throw new Error('Original candidate lacks independently observed offline .NET approval.');
            }
            receipt.stoppedStorage = { terminationSha256: hash(terminationBytes), inspectionSha256: hash(inspectionBytes), databases: 3, matchedRecords: 13 };
            receipt.offlineApprovalsSha256 = hash(approvalBytes);
            receipt.previousProcessId = prepared.processId;
        }
    }
    await writeFile(join(directory, 'evidence.json'), evidenceText + '\n');
    await writeFile(join(directory, 'server-observations.json'), JSON.stringify(host, null, 2) + '\n');
    await writeFile(join(directory, 'peer.json'), JSON.stringify(peerReceipt, null, 2) + '\n');
    await writeFile(join(directory, 'diagnostics.json'), diagnosticsText + '\n');
    receipt.interopEvidenceSha256 = hash(evidenceText + '\n'); receipt.dotnetProcessId = host.processId; receipt.interopBundleSha256 = peerReceipt.bundleSha256;
    receipt.interopDiagnosticsSha256 = hash(diagnosticsText + '\n');
}
await writeFile(join(directory, 'check.json'), JSON.stringify(receipt, null, 2) + '\n');
consumer.expoAndroidRuntimeChecks = { ...consumer.expoAndroidRuntimeChecks, [kind]: { status: receipt.status, report: `artifacts/native/android/runtime/${kind}/check.json`, passed, failed } };
consumer.expoNativeRuntime = `Android subsets: ${Object.entries(consumer.expoAndroidRuntimeChecks).map(([name, check]) => `${name} ${check.status}`).join(', ')}; full native acceptance and iOS remain pending`;
if (kind === 'local' && consumer.expoVectorFixtures) consumer.expoVectorFixtures.nativeRuntime = failed ? 'Android local suite failed; see the per-check report' : 'Android/Hermes portable subset passed (214 checks); iOS not tested';
if (consumer.expoAndroidNativeBuilds?.app) consumer.expoAndroidNativeBuilds.app.runtime = 'See expoAndroidRuntimeChecks; full native acceptance not complete';
await writeFile(consumerPath, JSON.stringify(consumer, null, 2) + '\n');
console.log(`Android ${kind}: ${passed} passed, ${failed} failed. Full native acceptance remains separate.`);
if (failed) { console.error(JSON.stringify(result.results.filter(row => !row.passed), null, 2)); process.exitCode = 1; }
