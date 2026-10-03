import { mkdir, readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { DatabaseSync } from 'node:sqlite';
import type { IncomingMessage, ServerResponse } from 'node:http';
import * as sdk from '@meshline/sdk';
import { GroupNetwork } from './group-network.js';
import { MessagingNetwork } from './messaging-network.js';
import { ChannelNetwork } from './channel-network.js';
import { DotnetBridge } from './dotnet.js';
import { context } from './relay-fixture.js';
import type { InteropFixture, InteropSnapshot } from '../portable/interop-models.js';

const hash = (value: string | Uint8Array) => createHash('sha256').update(value).digest('hex');
const operations = new Set(['authorize', 'close', 'message-start', 'group-start', 'group-create', 'group-invite', 'group-apply', 'group-approve',
    'group-recovery', 'group-recovery-approve', 'group-rotate', 'group-nickname', 'group-send', 'group-read', 'contact-add', 'contact-read', 'message-send', 'message-read',
    'channel-follow', 'channel-send', 'channel-edit', 'channel-read']);
interface Run {
    fixture: InteropFixture; network: GroupNetwork; bridge: DotnetBridge; directory: string; serial: number; ids: Set<string>; paths: string[];
    holds: Map<string, string>; heldResponses: Set<ServerResponse>; offlineApproving: boolean;
    snapshot: { run: string; processId: number; closed: boolean; requests: InteropSnapshot['requests'][number][]; faults: InteropSnapshot['faults'][number][];
        offlineApprovals: InteropSnapshot['offlineApprovals'][number][]; commands: InteropSnapshot['commands'][number][]; databases: InteropSnapshot['databases'][number][]; errors: string[] };
}

/** Controlled test relay. Native TypeScript uses HTTPS directly; the actual .NET
 * production SDK retains the existing stdin/HTTP bridge and its own SQLite files. */
export function createNativeInteropPeer(origin: string, directory: string, assembly: string) {
    const runs = new Map<string, Run>();
    async function request(run: Run, actor: 'typescript' | 'dotnet', url: string, init: sdk.RelayFetchInit): Promise<sdk.RelayFetchResponse> {
        try {
            const response = await run.network.network.fetch(url, init); const bytes = new Uint8Array(await response.arrayBuffer());
            const target = new URL(url); const method = target.pathname.slice(new URL(run.fixture.endpoint).pathname.length + 1).replaceAll('/', '.');
            const [, accountId, deviceId] = (init.headers['X-Meshline-Session'] ?? '').split('/');
            const page = method === 'message.timeline.sync' && response.status === 200 ? sdk.messageTimelinePageCodec.decode(JSON.parse(sdk.decodeUtf8(bytes))) : undefined;
            run.snapshot.requests.push({ actor, method: new URL(url).pathname.slice(new URL(run.fixture.endpoint).pathname.length + 1).replaceAll('/', '.'),
                at: new Date().toISOString(), ...(accountId ? { accountId } : {}), ...(deviceId ? { deviceId } : {}),
                ...(page ? { timeline: { after: Number(target.searchParams.get('after') ?? -1), sequences: page.items.map(item => item.sequence), messageIds: page.items.map(item => item.envelope.messageId) } } : {}),
                status: response.status, requestSha256: hash(JSON.stringify({ url, method: init.method, headers: init.headers, body: init.body })), bodySha256: hash(init.body ?? ''), responseSha256: hash(bytes) });
            return new Response(response.status === 204 ? null : bytes, { status: response.status, headers: response.headers.get('content-type') ? { 'content-type': response.headers.get('content-type')! } : {} });
        } catch (error) { run.snapshot.errors.push(`${actor} HTTP: ${String(error)}`); throw error; }
    }
    async function begin(id: string): Promise<InteropFixture> {
        if (!/^[a-f0-9]{24}$/.test(id) || runs.has(id)) throw new Error('Invalid or reused interop run ID');
        const endpoint = `${origin}/interop/${id}/v1`; const network = new GroupNetwork(new MessagingNetwork(endpoint));
        new ChannelNetwork(network.network);
        const fixture = { run: id, context: context.toString(), relayId: network.network.descriptor.relayId, endpoint, now: network.network.clock.wall };
        const runDirectory = join(directory, id); await mkdir(runDirectory, { recursive: true });
        let run: Run;
        const bridge = new DotnetBridge((url, init) => request(run, 'dotnet', url, init), assembly);
        if (!bridge.processId) { await bridge.dispose(); throw new Error('Missing .NET process ID'); }
        run = { fixture, network, bridge, directory: runDirectory, serial: 0, ids: new Set(), paths: [], holds: new Map(), heldResponses: new Set(), offlineApproving: false,
            snapshot: { run: id, processId: bridge.processId, closed: false, requests: [], faults: [], offlineApprovals: [], commands: [], databases: [], errors: [] } };
        runs.set(id, run); return fixture;
    }
    async function finish(run: Run) {
        if (run.snapshot.closed) return;
        try {
            for (const response of run.heldResponses) response.destroy();
            for (const id of run.ids) await run.bridge.invoke({ operation: 'close', id }); run.ids.clear();
            await run.bridge.dispose(); await run.network.dispose();
            for (const name of run.paths) {
                const path = join(run.directory, name); const database = new DatabaseSync(path, { readOnly: true }); const tables: Record<string, number> = {};
                try {
                    if (database.prepare('PRAGMA quick_check').get()?.quick_check !== 'ok') throw new Error('Closed .NET database failed integrity checking');
                    for (const table of ['Groups', 'GroupEvents', 'GroupMemberKeys', 'Messages', 'Channels', 'ChannelDescriptors', 'ChannelPosts']) tables[table] = Number(database.prepare(`SELECT count(*) AS count FROM ${table}`).get()!.count);
                } finally { database.close(); }
                const wal = existsSync(path + '-wal') ? { size: (await stat(path + '-wal')).size, sha256: hash(await readFile(path + '-wal')) } : undefined;
                run.snapshot.databases.push({ name, size: (await stat(path)).size, sha256: hash(await readFile(path)), tables, ...(wal ? { wal } : {}) });
            }
            run.snapshot.closed = true;
        } catch (error) { run.snapshot.errors.push(`finish: ${String(error)}`); throw error; }
    }
    async function approveOfflineMembers(run: Run) {
        if (run.snapshot.closed || run.offlineApproving || run.snapshot.offlineApprovals.length || run.snapshot.faults.length !== 5
            || run.snapshot.faults.some(value => value.disconnectedAt === undefined) || !run.ids.has('recovery')) throw new Error('Offline approvals require five terminated connections and the original .NET owner');
        run.offlineApproving = true;
        for (const recovery of [false, true]) {
            const fault = run.snapshot.faults.find(value => value.method === (recovery ? 'group.member.recovery.submit' : 'group.application.submit'))!;
            const entry = (recovery ? run.network.recoveries : run.network.applications).get(`${fault.resourceId}|${fault.account}`);
            if (!entry) throw new Error('Original member request disappeared before offline approval');
            const request = 'request' in entry ? entry.request : entry.application;
            const wire = 'request' in entry ? sdk.groupMemberRecoveryRequestCodec.encode(entry.request) : sdk.groupApplicationCodec.encode(entry.application);
            if (request.account !== fault.account || request.groupId !== fault.resourceId || hash(sdk.canonicalJson(wire)) !== fault.bodySha256) throw new Error('Offline approval candidate differs from the interrupted request');
            const group = { relayId: run.fixture.relayId, groupId: fault.resourceId };
            await command(run, { operation: recovery ? 'group-recovery-approve' : 'group-approve', id: 'recovery', ...group, accounts: [request.account] });
            const approvedAt = Date.now();
            const welcome = await command(run, { operation: 'group-send', id: 'recovery', ...group, text: recovery ? '.NET approved recovery while native process was absent' : '.NET approved admission while native process was absent' }) as { messageId: string };
            run.snapshot.offlineApprovals.push({ method: recovery ? 'group.member.recovery.approve' : 'group.application.approve', groupId: group.groupId,
                account: request.account, memberPublicKey: sdk.encodeBase64Url(request.memberEncryptionPublicKey), approvedAt, welcomeMessageId: welcome.messageId });
        }
        return run.snapshot.offlineApprovals;
    }
    async function command(run: Run, value: Record<string, unknown>): Promise<unknown> {
        const operation = String(value.operation); const id = String(value.id ?? '');
        if (operation === 'finish') { await finish(run); return run.snapshot; }
        if (run.snapshot.closed) throw new Error('Interop run is closed');
        if (operation === 'approve-offline-members') return approveOfflineMembers(run);
        if (operation === 'hold-recovery-responses') {
            if (run.holds.size || run.snapshot.faults.length) throw new Error('Recovery faults already armed');
            if (typeof value.messageId !== 'string' || typeof value.groupId !== 'string' || typeof value.channelId !== 'string'
                || typeof value.applicationGroupId !== 'string' || typeof value.recoveryGroupId !== 'string') throw new Error('Missing recovery fault identifiers');
            sdk.validateIdentifier('message', value.messageId); sdk.validateIdentifier('group', value.groupId); sdk.validateIdentifier('channel', value.channelId);
            sdk.validateIdentifier('group', value.applicationGroupId); sdk.validateIdentifier('group', value.recoveryGroupId);
            run.holds.set('message.send', value.messageId); run.holds.set('group.secret.rotation.commit', value.groupId); run.holds.set('channel.post', value.channelId);
            run.holds.set('group.application.submit', value.applicationGroupId); run.holds.set('group.member.recovery.submit', value.recoveryGroupId);
            return { armed: true };
        }
        if (operation === 'advance') {
            run.network.network.clock.tick(); for (const active of run.ids) await run.bridge.invoke({ operation: 'advance', id: active, seconds: 15 });
            return { now: run.network.network.clock.wall };
        }
        if (!/^[a-z][a-z0-9-]{0,31}$/.test(id)) throw new Error('Invalid .NET session ID');
        let result: unknown;
        if (operation === 'open') {
            if (!Number.isInteger(value.seed) || Number(value.seed) < 101 || Number(value.seed) > 120 || run.ids.has(id)) throw new Error('Invalid test account or duplicate session');
            const name = `${++run.serial}-${id}.sqlite`; const path = join(run.directory, name);
            result = await run.bridge.invoke({ operation, id, context: context.toString(), privateKey: Buffer.alloc(32, Number(value.seed)).toString('base64'), path,
                now: run.network.network.clock.wall, relays: [{ relayId: run.fixture.relayId, endpoint: run.fixture.endpoint }] });
            run.paths.push(name); run.ids.add(id);
        } else {
            if (!operations.has(operation) || !run.ids.has(id)) throw new Error('Unsupported command or missing .NET session');
            result = await run.bridge.invoke(value); if (operation === 'close') run.ids.delete(id);
        }
        run.snapshot.commands.push({ operation, id, resultSha256: hash(JSON.stringify(result)) }); return result;
    }
    return {
        snapshots: (): Record<string, InteropSnapshot> => Object.fromEntries([...runs].map(([id, run]) => [id, run.snapshot])),
        async approveOfflineMembers(id: string) { const run = runs.get(id); if (!run) throw new Error('Unknown recovery run'); return approveOfflineMembers(run); },
        async http(incoming: IncomingMessage, outgoing: ServerResponse, body: string): Promise<boolean> {
            const path = new URL(incoming.url!, origin).pathname;
            if (!path.startsWith('/interop/')) return false;
            outgoing.setHeader('content-type', 'application/json'); let run: Run | undefined;
            try {
                if (path === '/interop/begin' && incoming.method === 'POST') { outgoing.end(JSON.stringify(await begin(JSON.parse(body).run))); return true; }
                const match = /^\/interop\/([a-f0-9]{24})\/(command|observations|v1\/.*)$/.exec(path); if (!match) throw new Error('Unknown interop path');
                run = runs.get(match[1]!); if (!run) throw new Error('Unknown interop run');
                if (match[2] === 'observations' && incoming.method === 'GET') outgoing.end(JSON.stringify(run.snapshot));
                else if (match[2] === 'command' && incoming.method === 'POST') outgoing.end(JSON.stringify({ result: await command(run, JSON.parse(body)) }));
                else if (match[2]!.startsWith('v1/') && ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(incoming.method!)) {
                    const response = await request(run, 'typescript', origin + incoming.url!, { method: incoming.method as sdk.RelayFetchInit['method'], headers: { 'Content-Type': 'application/json',
                        ...(incoming.headers['x-meshline-session'] ? { 'X-Meshline-Session': String(incoming.headers['x-meshline-session']) } : {}) },
                        ...(body ? { body } : {}), signal: new AbortController().signal, credentials: 'omit', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer' });
                    const method = match[2]!.slice(3).replaceAll('/', '.'); const heldId = run.holds.get(method);
                    const document = body ? JSON.parse(body) : undefined;
                    if (heldId && response.status >= 200 && response.status < 300
                        && (method === 'message.send' ? document?.envelope?.message_id : method === 'channel.post' ? document?.channel_id : document?.group_id) === heldId) {
                        run.holds.delete(method); const fault: InteropSnapshot['faults'][number] = { method, resourceId: heldId, ...(typeof document?.account === 'string' ? { account: document.account } : {}), bodySha256: hash(body), acceptedAt: Date.now() };
                        run.snapshot.faults.push(fault); run.heldResponses.add(outgoing);
                        outgoing.once('close', () => { fault.disconnectedAt = Date.now(); run!.heldResponses.delete(outgoing); });
                        // Release OkHttp's per-host dispatcher slot after headers;
                        // five headerless holds would also starve the native test
                        // control requests. A 204 is acknowledged by its headers
                        // alone, so those two responses must remain headerless.
                        // Keep the other three bodies unsent until termination.
                        if (response.status !== 204) { outgoing.statusCode = response.status; outgoing.flushHeaders(); }
                    } else { outgoing.statusCode = response.status; outgoing.end(response.status === 204 ? undefined : Buffer.from(await response.arrayBuffer())); }
                } else throw new Error(`Unsupported interop request: ${incoming.method} ${path}`);
            } catch (error) { run?.snapshot.errors.push(String(error)); outgoing.statusCode = 500; outgoing.end(JSON.stringify({ code: 'bad_gateway', message: String(error) })); }
            return true;
        },
        async dispose() { for (const run of runs.values()) await finish(run); },
    };
}
