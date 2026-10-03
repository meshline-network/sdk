import { ExpoSqliteStore, expoRandom, expoRelayFetch } from '@meshline/expo';
import { File, Paths } from 'expo-file-system';
import { prepareProcessRecovery, verifyProcessRecovery, type ProcessRecoveryState } from '../portable/process-recovery.js';
import type { VectorResult } from './portable-vectors';
import { decodeUtf8, type RelayFetch } from '@meshline/sdk';

// Retain open native SQLite and pending HTTPS operations until Android terminates
// the process. Preparing again or verifying in this process would invalidate it.
let pending: Awaited<ReturnType<typeof prepareProcessRecovery>> | undefined;
function options(mode: string) {
    const diagnostics: string[] = []; const file = new File(Paths.document, `meshline-native-${mode}-diagnostics.json`); file.write('[]');
    // Fixture-only driver tracing: record connection lifetimes and SQL templates,
    // never SQL parameters (which may contain protected keys or message bodies).
    const traceFile = new File(Paths.document, `meshline-native-${mode}-sqlite-trace.json`);
    const recent: Record<string, unknown>[] = []; const failures: Record<string, unknown>[] = []; let nextConnection = 0;
    const network: Record<string, unknown>[] = []; const networkIssues: Record<string, unknown>[] = []; const pendingNetwork = new Set<Record<string, unknown>>();
    const networkFile = new File(Paths.document, `meshline-native-${mode}-network-trace.json`);
    const tracedFetch: RelayFetch = async (url, init) => {
        const entry: Record<string, unknown> = { url, method: init.method, startedAt: new Date().toISOString(), phase: 'headers' };
        // This bounded acceptance run retains every response, including early
        // failures that would otherwise leave the trace before disposal.
        pendingNetwork.add(entry); network.push(entry);
        const finish = (error?: unknown) => { entry.finishedAt = new Date().toISOString(); entry.lastPhase = entry.phase; entry.phase = error === undefined ? 'completed' : 'failed'; if (error !== undefined) entry.error = String(error); if (error !== undefined || Date.now() - Date.parse(String(entry.startedAt)) > 10000) networkIssues.push(entry); pendingNetwork.delete(entry); };
        try {
            const response = await expoRelayFetch(url, init); entry.headersAt = new Date().toISOString(); entry.status = response.status; entry.phase = 'body';
            return { ...response, ...(response.body ? { body: { async cancel() { try { await response.body!.cancel(); finish(); } catch (error) { finish(error); throw error; } } } } : {}),
                async arrayBuffer() {
                    try {
                        const bytes = await response.arrayBuffer(); entry.bytes = bytes.byteLength;
                        entry.contentLength = response.headers.get('content-length');
                        if (response.status === 200) {
                            try { JSON.parse(decodeUtf8(new Uint8Array(bytes))); }
                            catch (error) { entry.invalidJson = String(error); entry.prefixBytes = [...new Uint8Array(bytes).slice(0, 32)]; networkIssues.push(entry); }
                        }
                        finish(); return bytes;
                    } catch (error) { finish(error); throw error; }
                } };
        } catch (error) { finish(error); throw error; }
    };
    const capture = () => {
        traceFile.write(JSON.stringify({ mode, recent, failures }, null, 2));
        networkFile.write(JSON.stringify({ mode, recent: network, issues: networkIssues, pending: [...pendingNetwork] }, null, 2));
    };
    traceFile.write(JSON.stringify({ mode, recent, failures }));
    const createStore = (databaseName: string) => {
        const store = new ExpoSqliteStore({ databaseName }); const open = store.runtime.open.bind(store.runtime);
        store.runtime.open = async () => {
            const database = await open(); const connection = ++nextConnection; let active = 0; let closed = false;
            const invoke = async <T>(method: string, sql: string | undefined, operation: () => Promise<T>): Promise<T> => {
                const entry: Record<string, unknown> = { databaseName, connection, method, sql, startedAt: new Date().toISOString(), active: ++active, closed };
                recent.push(entry); if (recent.length > 64) recent.shift();
                try { const result = await operation(); entry.outcome = 'passed'; return result; }
                catch (error) {
                    entry.outcome = 'failed'; entry.error = error instanceof Error ? error.stack ?? String(error) : String(error);
                    failures.push({ ...entry }); traceFile.write(JSON.stringify({ mode, recent, failures }, null, 2)); throw error;
                } finally { active--; entry.finishedAt = new Date().toISOString(); }
            };
            return {
                execAsync: sql => invoke('execAsync', sql, () => database.execAsync(sql)),
                runAsync: (sql, ...parameters) => invoke('runAsync', sql, () => database.runAsync(sql, ...parameters)),
                getFirstAsync: <T>(sql: string, ...parameters: (string | number | null)[]) => invoke('getFirstAsync', sql, () => database.getFirstAsync<T>(sql, ...parameters)),
                getAllAsync: <T>(sql: string, ...parameters: (string | number | null)[]) => invoke('getAllAsync', sql, () => database.getAllAsync<T>(sql, ...parameters)),
                closeAsync: () => invoke('closeAsync', undefined, async () => { await database.closeAsync(); closed = true; }),
            };
        };
        return store;
    };
    return { origin: 'https://127.0.0.1:18445', fetch: tracedFetch, random: expoRandom, createStore, capture,
        onDiagnostic: (message: string) => { diagnostics.push(message); file.write(JSON.stringify(diagnostics, null, 2)); capture(); } };
}
export async function prepareNativeProcessRecovery(packageManifestSha256: string, acceptanceFixtureSha256: string, record: (value: VectorResult) => void) {
    if (pending) throw new Error('Terminate and relaunch the application before another recovery check.');
    pending = await prepareProcessRecovery(options('prepare-recovery'), record);
    new File(Paths.document, 'meshline-process-recovery-state.json').write(JSON.stringify({ packageManifestSha256, acceptanceFixtureSha256, state: pending.state }, null, 2));
    new File(Paths.document, 'meshline-native-prepare-recovery-evidence.json').write(JSON.stringify(pending.snapshot, null, 2));
}
export async function verifyNativeProcessRecovery(packageManifestSha256: string, acceptanceFixtureSha256: string, record: (value: VectorResult) => void) {
    if (pending) throw new Error('Recovery must run in a new application process.');
    const prepared = JSON.parse(await new File(Paths.document, 'meshline-process-recovery-state.json').text()) as { packageManifestSha256: string; acceptanceFixtureSha256: string; state: ProcessRecoveryState };
    if (prepared.packageManifestSha256 !== packageManifestSha256 || prepared.acceptanceFixtureSha256 !== acceptanceFixtureSha256) throw new Error('Prepared process state belongs to different packages or fixture sources.');
    const runtime = options('recovery');
    try { await verifyProcessRecovery(runtime, prepared.state, record, evidence => { new File(Paths.document, 'meshline-native-recovery-evidence.json').write(JSON.stringify(evidence, null, 2)); }); }
    finally { runtime.capture(); }
}
