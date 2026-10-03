import { useState } from 'react';
import { Button, Platform, SafeAreaView, ScrollView, Text } from 'react-native';
import { MeshlineClient, relayOrigin, encodeBase64Url } from '@meshline/sdk';
import { ExpoSqliteStore, expoRandom, createExpoSocketFactory } from '@meshline/expo';
import { File, Paths } from 'expo-file-system';
import { runPortableVectors, type VectorFile, type VectorResult } from './portable-vectors';
import { runTransportChecks } from './transport-checks';
import { prepareRestart, verifyRestart } from './restart-checks';
import { runWorkflowChecks } from './workflow-checks';
import { runLifecycleChecks } from './lifecycle-checks';
import { runNativeReconnectChecks } from './reconnect-checks';
import { runNativeInteropChecks } from './interop-checks';
import { prepareNativeProcessRecovery, verifyNativeProcessRecovery } from './process-recovery-checks';
import { concurrentNativeStatements, releasedNativeStatement } from './sqlite-registry-checks';

// Public test identity only. Network checks contact only the local TLS fixture.
const binding = { context: 'neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70', accountId: 'neo:860833102:NgaxELHoZFpQWNwd74Fvq4wF3qz57WTfpp' };
function check(condition: boolean, message: string): void { if (!condition) throw new Error(message); }

async function sqliteCheck(): Promise<void> {
    const name = 'meshline-native-acceptance #中文%.sqlite'; const store = new ExpoSqliteStore({ databaseName: name });
    const marker = encodeBase64Url(expoRandom.bytes(32));
    try {
        await store.migrate(); await store.initialize(binding);
        const before = await store.read([]);
        await store.commit(before.version, [{ kind: 'put', collection: 'acceptance', key: 'run', value: { marker, version: 1 } }]);
        const reader = await store.openQuery({ collection: 'acceptance', key: 'run' });
        try {
            const current = await store.read([]);
            await store.commit(current.version, [{ kind: 'put', collection: 'acceptance', key: 'run', value: { marker, version: 2 } }]);
            check((await reader.readNext(1))[0]?.value.version === 1, 'SQLite reader did not keep its snapshot.');
        } finally { await reader.dispose(); }
    } finally { await store.dispose(); }
    const reopened = new ExpoSqliteStore({ databaseName: name });
    try {
        await reopened.initialize(binding);
        const saved = (await reopened.read([{ collection: 'acceptance', key: 'run' }])).sets[0]?.[0]?.value;
        check(saved?.marker === marker && saved.version === 2, 'Native SQLite reopen lost committed data.');
    } finally { await reopened.dispose(); }
}

// A native timer lets rendering/input run between CPU-bound vector checks.
const yieldToUi = () => new Promise<void>(resolve => setTimeout(resolve, 0));
async function nativeChecks(onResult: (result: VectorResult) => void): Promise<VectorResult[]> {
    const checks: readonly [string, () => void | Promise<void>][] = [
        ['Hermes runtime', () => check(typeof (globalThis as { HermesInternal?: unknown }).HermesInternal !== 'undefined', 'Hermes is not active.')],
        ['SDK entry point', () => check(typeof MeshlineClient === 'function', 'Core SDK entry point was not linked.')],
        ['IDNA / IPv6 origins', () => {
            check(relayOrigin('https://faß.example:443/path') === 'https://xn--fa-hia.example', 'IDNA origin differs.');
            check(relayOrigin('wss://[2001:0DB8:0:0:0:0:0:1]:8443/path') === 'https://[2001:db8::1]:8443', 'IPv6 origin differs.');
        }],
        ['native random source', () => check(expoRandom.bytes(32).length === 32, 'Native random output size differs.')],
        // Module presence only: this does not establish TLS, cookie or redirect behavior.
        ['native socket module presence', () => check(typeof createExpoSocketFactory() === 'function', 'Native WebSocket module did not load.')],
        ['SQLite snapshot / reopen', sqliteCheck],
        ['SQLite registry / 8192 concurrent statements', concurrentNativeStatements],
        ['SQLite registry / real release rejects access', releasedNativeStatement],
    ];
    const results: VectorResult[] = [];
    for (const [name, run] of checks) {
        await yieldToUi(); let result: VectorResult;
        try { await run(); result = { name, passed: true }; }
        catch (error) { result = { name, passed: false, error: String(error) }; }
        results.push(result); onResult(result);
    }
    return results;
}

export default function App({ vectors, packageManifestSha256, acceptanceFixtureSha256 }: { readonly vectors: Readonly<Record<string, VectorFile>>; readonly packageManifestSha256: string; readonly acceptanceFixtureSha256: string }) {
    const [running, setRunning] = useState(false); const [output, setOutput] = useState('Local native checks have not run.');
    async function run(mode: 'local' | 'transport' | 'prepare-restart' | 'restart' | 'workflows' | 'lifecycle' | 'reconnect' | 'interop' | 'prepare-recovery' | 'recovery' = 'local') {
        setRunning(true);
        const hermes = typeof (globalThis as { HermesInternal?: unknown }).HermesInternal !== 'undefined';
        const startedAt = new Date().toISOString(); const results: VectorResult[] = [];
        setOutput(`${startedAt} | ${Platform.OS} ${String(Platform.Version)} | Hermes: ${hermes}\nPackage manifest SHA-256: ${packageManifestSha256}`);
        const record = (result: VectorResult) => {
            results.push(result);
            setOutput(previous => `${previous}\n${result.passed ? 'PASS' : 'FAIL'}: ${result.name}${result.error ? ` — ${result.error}` : ''}`);
        };
        try {
            if (mode === 'transport') await runTransportChecks(record);
            else if (mode === 'workflows') await runWorkflowChecks(record);
            else if (mode === 'lifecycle') await runLifecycleChecks(record);
            else if (mode === 'reconnect') await runNativeReconnectChecks(record);
            else if (mode === 'interop') await runNativeInteropChecks(record);
            else if (mode === 'prepare-recovery') await prepareNativeProcessRecovery(packageManifestSha256, acceptanceFixtureSha256, record);
            else if (mode === 'recovery') await verifyNativeProcessRecovery(packageManifestSha256, acceptanceFixtureSha256, record);
            else if (mode === 'prepare-restart') { await prepareRestart(packageManifestSha256); record({ name: 'restart state prepared; close and relaunch the app before verification', passed: true }); }
            else if (mode === 'restart') await verifyRestart(packageManifestSha256, record);
            else { await nativeChecks(record); await runPortableVectors(vectors, record, yieldToUi, expoRandom); }
        } catch (error) {
            record({ name: 'acceptance runner', passed: false, error: error instanceof Error ? error.stack ?? String(error) : String(error) });
        } finally {
            const passed = results.filter(value => value.passed).length; const failed = results.filter(value => !value.passed).length;
            const report = { startedAt, finishedAt: new Date().toISOString(), platform: Platform.OS, osVersion: Platform.Version, hermes,
                packageManifestSha256, acceptanceFixtureSha256, scope: mode, passed, failed, results };
            const file = mode === 'local' ? 'meshline-native-acceptance.json' : `meshline-native-${mode}.json`;
            try {
                new File(Paths.document, file).write(JSON.stringify(report, null, 2));
                setOutput(previous => `${previous}\nCompleted: ${passed} passed, ${failed} failed. Full relay/lifecycle acceptance remains separate.`);
            } finally { setRunning(false); }
        }
    }
    return <SafeAreaView style={{ flex: 1, padding: 24 }}><ScrollView>
        <Text accessibilityRole="header">Meshline SDK native acceptance</Text>
        <Text>Local checks run offline. Transport checks require the app-scoped test CA and local TLS peer. Full relay workflows and suspend/resume remain separate.</Text>
        <Button title="Run local native checks" disabled={running} onPress={() => { void run(); }} />
        <Button title="Run loopback transport checks" disabled={running} onPress={() => { void run('transport'); }} />
        <Button title="Run SQLite recovery workflows" disabled={running} onPress={() => { void run('workflows'); }} />
        <Button title="Run client background/resume" disabled={running} onPress={() => { void run('lifecycle'); }} />
        <Button title="Run WSS reconnect / catch-up" disabled={running} onPress={() => { void run('reconnect'); }} />
        <Button title="Run .NET group interoperability" disabled={running} onPress={() => { void run('interop'); }} />
        <Button title="Prepare process restart" disabled={running} onPress={() => { void run('prepare-restart'); }} />
        <Button title="Verify after process restart" disabled={running} onPress={() => { void run('restart'); }} />
        <Button title="Prepare interrupted operations" disabled={running} onPress={() => { void run('prepare-recovery'); }} />
        <Button title="Recover interrupted operations" disabled={running} onPress={() => { void run('recovery'); }} />
        <Text selectable>{output}</Text>
    </ScrollView></SafeAreaView>;
}
