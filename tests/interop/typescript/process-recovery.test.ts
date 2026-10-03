import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from 'vitest';
import { build } from 'vite';
import { createNativeInteropPeer } from '../../../typescript/tests/support/native-interop-peer.js';
import { startTlsPeer, tlsMaterial } from '../../../typescript/tests/support/tls-peer.js';
import { removeTestDirectory } from '../../../typescript/tests/support/temp.js';
import type { InteropResult } from '../../../typescript/tests/portable/interop.js';
import type { ProcessRecoveryState } from '../../../typescript/tests/portable/process-recovery.js';

test('a genuinely terminated process recovers accepted direct, rotation, publication, admission and member-key requests with the actual .NET SDK', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'meshline-process-recovery-')); const material = await tlsMaterial();
    let fixture: ReturnType<typeof createNativeInteropPeer>;
    const peer = await startTlsPeer(material, async (request, response) => { if (!await fixture.http(request, response, peer.requests.at(-1)!.body)) { response.statusCode = 404; response.end(); } });
    fixture = createNativeInteropPeer(peer.https, join(directory, 'dotnet'), fileURLToPath(new URL('../dotnet/bin/Release/net10.0/Meshline.Interop.dll', import.meta.url)));
    const workers: ReturnType<typeof spawn>[] = [];
    async function worker(phase: 'prepare' | 'verify') {
        const config = join(directory, `${phase}.json`); await writeFile(config, JSON.stringify({ origin: peer.https, ca: material.ca, directory, phase }));
        const child = spawn(process.execPath, [join(directory, 'worker.mjs'), config], { stdio: 'pipe', windowsHide: true }); workers.push(child);
        let output = ''; child.stdout!.on('data', value => { output += String(value); }); child.stderr!.on('data', value => { output += String(value); });
        const exited = new Promise<number | null>((resolve, reject) => { child.once('error', reject); child.once('exit', code => resolve(code)); });
        return { child, exited, output: () => output };
    }
    try {
        await build({ configFile: false, logLevel: 'silent', ssr: { noExternal: true }, build: { ssr: fileURLToPath(new URL('../../../typescript/tests/support/process-recovery-child.ts', import.meta.url)), outDir: directory, emptyOutDir: false,
            rollupOptions: { output: { entryFileNames: 'worker.mjs' } } } });
        const original = await worker('prepare'); const deadline = Date.now() + 120000;
        while (!original.output().includes('PROCESS-READY')) {
            if (original.child.exitCode !== null || Date.now() > deadline) throw new Error(`Preparation failed: ${original.output()}`);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        const prepared = JSON.parse(await readFile(join(directory, 'prepared.json'), 'utf8')) as { state: ProcessRecoveryState; results: InteropResult[] };
        expect(prepared.results).toHaveLength(11); expect(prepared.results.every(value => value.passed)).toBe(true);
        expect(prepared.state.persisted).toHaveLength(7);
        expect(prepared.state.members.map(value => [value.kind, value.persisted.length])).toEqual([['application', 3], ['recovery', 3]]);
        expect(fixture.snapshots()[prepared.state.fixture.run]!.faults).toHaveLength(5);
        expect(fixture.snapshots()[prepared.state.fixture.run]!.faults.every(value => value.disconnectedAt === undefined)).toBe(true);
        expect(original.child.kill('SIGKILL')).toBe(true); await original.exited;
        const disconnected = Date.now() + 5000;
        while (fixture.snapshots()[prepared.state.fixture.run]!.faults.some(value => value.disconnectedAt === undefined)) {
            if (Date.now() > disconnected) throw new Error('Terminated process connections stayed open'); await new Promise(resolve => setTimeout(resolve, 25));
        }
        await fixture.approveOfflineMembers(prepared.state.fixture.run);
        const approved = fixture.snapshots()[prepared.state.fixture.run]!;
        expect(approved.offlineApprovals).toHaveLength(2);
        for (const member of prepared.state.members) expect(approved.offlineApprovals).toContainEqual(expect.objectContaining({
            method: member.kind === 'application' ? 'group.application.approve' : 'group.member.recovery.approve',
            groupId: member.group.groupId, account: member.accountId, memberPublicKey: member.candidatePublicKey,
            approvedAt: expect.any(Number), welcomeMessageId: expect.any(String)
        }));
        expect(approved.offlineApprovals.every(value => value.approvedAt >= Math.max(...approved.faults.map(fault => fault.disconnectedAt!)))).toBe(true);
        const resumed = await worker('verify'); expect(resumed.child.pid).not.toBe(original.child.pid);
        expect(await resumed.exited, resumed.output()).toBe(0);
        const results = JSON.parse(await readFile(join(directory, 'verified.json'), 'utf8')) as InteropResult[];
        expect(results).toHaveLength(25); expect(results.every(value => value.passed)).toBe(true); expect(peer.failures).toEqual([]);
        expect(fixture.snapshots()[prepared.state.fixture.run]).toMatchObject({ closed: true, errors: [] });
    } finally {
        for (const child of workers) if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
        await fixture.dispose(); await peer.close(); await removeTestDirectory(directory);
    }
}, 240000);
