import { readFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { request } from 'node:https';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const [serial] = process.argv.slice(2);
if (!serial || !/^[\w.:-]+$/.test(serial) || process.argv.length !== 3) throw new Error('Usage: node scripts/approve-stopped-android-members.mjs <adb-serial>');
const directory = join(root, 'artifacts/native/android/runtime/prepare-recovery');
const read = name => readFileSync(join(directory, name));
const stateBytes = read('state.json'); const state = JSON.parse(stateBytes).state;
const preparedBytes = read('check.json'); const prepared = JSON.parse(preparedBytes);
const terminationBytes = read('termination.json'); const termination = JSON.parse(terminationBytes);
const inspectionBytes = read('native-storage-inspection.json'); const inspection = JSON.parse(inspectionBytes);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const consumer = JSON.parse(readFileSync(join(root, 'artifacts/packages/consumer-check.json'), 'utf8'));
const peer = JSON.parse(readFileSync(join(root, 'artifacts/native/interop/peer.json'), 'utf8'));
const ca = readFileSync(join(root, 'artifacts/native/tls/ca.pem'));
if (prepared.status !== 'passed' || prepared.serial !== serial || prepared.recoveryStateSha256 !== hash(stateBytes)
    || prepared.packageManifestSha256 !== consumer.packageManifestSha256 || prepared.acceptanceFixtureSha256 !== consumer.expoAcceptanceFixtureSha256
    || termination.serial !== serial || !termination.processAbsent || termination.originalProcessId !== prepared.processId
    || termination.preparationCheckSha256 !== hash(preparedBytes) || termination.recoveryStateSha256 !== hash(stateBytes)
    || inspection.terminationSha256 !== hash(terminationBytes) || inspection.databases?.length !== 3 || inspection.rows?.length !== 13
    || inspection.integrity !== 'ok' || !inspection.durableFileHashesMatch || inspection.rows.some(value => value.matches !== true)
    || hash(ca) !== peer.caSha256 || !/^[a-f0-9]{24}$/.test(state.fixture.run)) throw new Error('Stopped process, storage or TLS evidence is stale or incomplete');
const adb = resolve(process.env.ANDROID_HOME || join(root, 'artifacts/toolchains/android-sdk'), 'platform-tools', process.platform === 'win32' ? 'adb.exe' : 'adb');
function assertAbsent() {
    const value = spawnSync(adb, ['-s', serial, 'shell', 'pidof', 'org.meshline.sdkacceptance'], { encoding: 'utf8', windowsHide: true, timeout: 15000 });
    if (value.error || value.status !== 1 || value.stdout.trim() || value.stderr.trim()) throw new Error('Native process is running or its absence cannot be confirmed');
}
const endpoint = new URL(state.fixture.endpoint);
if (endpoint.protocol !== 'https:' || endpoint.hostname !== '127.0.0.1' || endpoint.port !== String(peer.port)) throw new Error('Only the bound loopback TLS peer is allowed');
const absentBeforeAt = new Date().toISOString(); assertAbsent();
const url = new URL(`/interop/${state.fixture.run}/command`, endpoint);
const approvals = await new Promise((resolveResult, reject) => {
    const body = JSON.stringify({ operation: 'approve-offline-members' });
    const req = request(url, { ca, method: 'POST', headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }, timeout: 60000 }, response => {
        const chunks = []; response.on('data', chunk => chunks.push(chunk)); response.on('error', reject);
        response.on('end', () => {
            try { const text = Buffer.concat(chunks).toString('utf8'); if (response.statusCode !== 200) throw new Error(`Offline approval failed (${response.statusCode}): ${text}`); resolveResult(JSON.parse(text).result); }
            catch (error) { reject(error); }
        });
    }); req.on('timeout', () => req.destroy(new Error('Offline approval deadline'))); req.on('error', reject); req.end(body);
});
assertAbsent(); const absentAfterAt = new Date().toISOString();
if (!Array.isArray(approvals) || approvals.length !== 2 || state.members.some(member => !approvals.some(value =>
    value.method === (member.kind === 'application' ? 'group.application.approve' : 'group.member.recovery.approve')
    && value.groupId === member.group.groupId && value.account === member.accountId && value.memberPublicKey === member.candidatePublicKey
    && value.approvedAt >= Date.parse(absentBeforeAt) && value.approvedAt <= Date.parse(absentAfterAt)))) throw new Error('The .NET owner did not approve the original candidates while the native process was absent');
const receipt = { status: 'passed', run: state.fixture.run, serial, previousProcessId: prepared.processId, processAbsentBefore: true, processAbsentAfter: true,
    absentBeforeAt, absentAfterAt, packageManifestSha256: consumer.packageManifestSha256, acceptanceFixtureSha256: consumer.expoAcceptanceFixtureSha256,
    recoveryStateSha256: hash(stateBytes), preparationCheckSha256: hash(preparedBytes), terminationSha256: hash(terminationBytes), inspectionSha256: hash(inspectionBytes), approvals };
writeFileSync(join(directory, 'offline-approvals.json'), JSON.stringify(receipt, null, 2) + '\n');
console.log(JSON.stringify({ processAbsent: true, originalProcessId: prepared.processId, approvedMembers: approvals.length, run: state.fixture.run }));
