import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { fileURLToPath } from 'node:url';
import { resolve, join } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const [serial] = process.argv.slice(2);
if (!serial || !/^[\w.:-]+$/.test(serial) || process.argv.length !== 3) throw new Error('Usage: node scripts/capture-stopped-android-storage.mjs <adb-serial>');
const directory = join(root, 'artifacts/native/android/runtime/prepare-recovery');
const stateBytes = readFileSync(join(directory, 'state.json')); const prepared = JSON.parse(stateBytes);
const checkBytes = readFileSync(join(directory, 'check.json')); const receipt = JSON.parse(checkBytes);
const consumer = JSON.parse(readFileSync(join(root, 'artifacts/packages/consumer-check.json'), 'utf8'));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
if (receipt.status !== 'passed' || receipt.serial !== serial || receipt.recoveryStateSha256 !== hash(stateBytes)
    || receipt.packageManifestSha256 !== consumer.packageManifestSha256 || prepared.acceptanceFixtureSha256 !== consumer.expoAcceptanceFixtureSha256) throw new Error('Preparation receipt is stale');
const adb = resolve(process.env.ANDROID_HOME || join(root, 'artifacts/toolchains/android-sdk'), 'platform-tools', process.platform === 'win32' ? 'adb.exe' : 'adb');
function assertAbsent() {
    const stopped = spawnSync(adb, ['-s', serial, 'shell', 'pidof', 'org.meshline.sdkacceptance'], { encoding: 'utf8', windowsHide: true, timeout: 15000 });
    if (stopped.error || stopped.status !== 1 || stopped.stdout.trim() || stopped.stderr.trim()) throw new Error('App process absence was not confirmed');
}
assertAbsent(); const firstAbsentAt = new Date().toISOString();
const state = prepared.state; const run = state.fixture.run;
if (!/^[a-f0-9]{24}$/.test(run) || state.database !== `meshline-process-${run}.sqlite` || state.persisted.length !== 7
    || state.members?.length !== 2 || state.members.some((member, index) => member.kind !== ['application', 'recovery'][index]
        || member.database !== `meshline-process-${run}-${member.kind}.sqlite` || member.persisted.length !== 3)) throw new Error('Unexpected process recovery stores');
const stores = [{ ...state, kind: 'main' }, ...state.members]; const files = [];
for (const store of stores) for (const suffix of ['', '-wal', '-shm']) {
    const bytes = execFileSync(adb, ['-s', serial, 'exec-out', 'run-as', 'org.meshline.sdkacceptance', 'cat', `files/SQLite/${store.database}${suffix}`], { windowsHide: true, maxBuffer: 16 * 1024 * 1024, timeout: 15000 });
    const file = `${store.kind}-before-recovery.sqlite${suffix}`;
    writeFileSync(join(directory, file), bytes); files.push({ database: store.database, file, size: bytes.length, sha256: hash(bytes) });
}
assertAbsent();
const termination = { at: new Date().toISOString(), firstAbsentAt, serial, originalProcessId: receipt.processId, processAbsent: true,
    method: 'External Android process termination; pidof confirmed absence before and after storage capture',
    packageManifestSha256: consumer.packageManifestSha256, acceptanceFixtureSha256: consumer.expoAcceptanceFixtureSha256,
    preparationCheckSha256: hash(checkBytes), recoveryStateSha256: hash(stateBytes), files };
const terminationBytes = JSON.stringify(termination, null, 2) + '\n'; writeFileSync(join(directory, 'termination.json'), terminationBytes);
const canonical = value => value === null || typeof value !== 'object' ? JSON.stringify(value) : Array.isArray(value)
    ? '[' + value.map(canonical).join(',') + ']' : '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}';
const databases = []; const rows = [];
for (const store of stores) {
    const db = new DatabaseSync(join(directory, `${store.kind}-before-recovery.sqlite`), { readOnly: true });
    try {
        const integrity = Object.values(db.prepare('PRAGMA quick_check').get())[0];
        if (integrity !== 'ok') throw new Error('Stopped native database failed integrity check: ' + store.kind);
        for (const item of store.persisted) {
            const record = db.prepare('SELECT value FROM meshline_records WHERE collection=? AND key=?').get(item.query.collection, item.query.key);
            if (!record) throw new Error('Missing persisted record: ' + item.query.collection);
            const value = JSON.parse(record.value); const actual = hash(canonical(value));
            if (actual !== item.sha256) throw new Error('Persisted fingerprint mismatch: ' + item.query.collection);
            rows.push({ database: store.database, query: item.query, sha256: actual, matches: true,
                ...(value.state ? { state: value.state } : {}), protectedMaterial: Object.keys(value).some(key => key.startsWith('protected')) });
        }
        databases.push({ name: store.database, integrity, matchedRecords: store.persisted.length });
    } finally { db.close(); }
}
const durableFileHashesMatch = files.filter(file => !file.file.endsWith('-shm')).every(file => hash(readFileSync(join(directory, file.file))) === file.sha256);
if (!durableFileHashesMatch) throw new Error('Read-only inspection changed durable database/WAL bytes');
assertAbsent();
const report = { checkedAt: new Date().toISOString(), terminationSha256: hash(terminationBytes), integrity: 'ok', databases, rows, durableFileHashesMatch };
writeFileSync(join(directory, 'native-storage-inspection.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ processAbsent: true, originalProcessId: receipt.processId, databases: databases.length, matchedRecords: rows.length, durableFileHashesMatch }));
