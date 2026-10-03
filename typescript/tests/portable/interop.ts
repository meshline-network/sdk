import * as sdk from '@meshline/sdk';
import { MessagingClock } from './clock.js';
import type { InteropFixture, InteropSnapshot } from './interop-models.js';

export interface InteropOptions {
    readonly origin: string; readonly fetch: sdk.RelayFetch; readonly random: sdk.RandomSource;
    createStore(name: string): sdk.MeshlineStore;
    onDiagnostic?(message: string): void;
    onWait?(value: { phase: string; elapsedMs: number; advances: number; complete: boolean }): void;
}
export interface InteropResult { readonly name: string; readonly passed: boolean; readonly error?: string }
interface GroupRead { membership: string; messages: { messageId: string; text: string; sequence: number }[]; members: { accountId: string; nickname: string | null }[] }
const check = (value: unknown, message: string): void => { if (!value) throw new Error(message); };
const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
async function all<T>(query: Promise<sdk.QueryReader<T>>): Promise<T[]> {
    const reader = await query; try { const rows: T[] = []; for (;;) { const page = await reader.readNext(100); if (!page.length) return rows; rows.push(...page); } } finally { await reader.dispose(); }
}

/** Public managers execute here, on whichever JS runtime is under acceptance.
 * The peer only hosts the controlled relay and actual .NET production SDK process. */
export async function runInteropChecks(options: InteropOptions, onResult: (result: InteropResult) => void, onEvidence?: (value: InteropSnapshot) => void | Promise<void>): Promise<void> {
    const origin = new URL(options.origin); if (origin.protocol !== 'https:' || origin.hostname !== '127.0.0.1') throw new Error('Only the loopback TLS acceptance peer is allowed');
    const run = [...options.random.bytes(12)].map(value => value.toString(16).padStart(2, '0')).join('');
    async function json<T>(path: string, value?: unknown): Promise<T> {
        const scope = sdk.abortScope([], 60000);
        try {
            const response = await options.fetch(new URL(path, origin).href, { method: value === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json' },
                ...(value === undefined ? {} : { body: JSON.stringify(value) }), signal: scope.signal, redirect: 'error', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer' });
            const text = sdk.decodeUtf8(new Uint8Array(await response.arrayBuffer())); if (response.status !== 200) throw new Error(`Interop control failed (${response.status}): ${text}`); return JSON.parse(text) as T;
        } finally { scope.dispose(); }
    }
    const fixture = await json<InteropFixture>('/interop/begin', { run }); const context = sdk.NetworkContext.parse(fixture.context);
    const command = async <T = unknown>(request: Record<string, unknown>): Promise<T> => (await json<{ result: T }>(`/interop/${run}/command`, request)).result;
    const clock = new MessagingClock(); clock.wall = fixture.now;
    const resources: { dispose(): Promise<void> }[] = []; const errors: string[] = []; let serial = 0;
    const pass = (name: string) => onResult({ name, passed: true });
    async function open(seed: number, name = `meshline-interop-${run}-${++serial}.sqlite`) {
        const key = new Uint8Array(32).fill(seed); const publicKey = sdk.accountPublicKey(key); const accountId = sdk.getAccountId('neo:860833102', publicKey);
        const store = options.createStore(name); await store.migrate();
        const entry: sdk.RelayEntry = { relayId: fixture.relayId, endpoint: fixture.endpoint, status: 'active', updatedAt: BigInt(fixture.now) * 1000n };
        const registry: sdk.RelayRegistry = { context, getRelay: async id => id === entry.relayId ? entry : undefined, getRelays: () => (async function* () { yield entry; })() };
        const signer: sdk.AccountSigner = { accountId, publicKey, sign: async input => sdk.signAccount(input, key, options.random) };
        const protector: sdk.SecretProtector = { protect: async (bytes, purpose) => { const nonce = options.random.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(key, nonce, bytes, sdk.encodeUtf8(purpose))); },
            unprotect: async (bytes, purpose) => sdk.decryptAes(key, bytes.slice(0, 12), bytes.slice(12), sdk.encodeUtf8(purpose)) };
        const pool = new sdk.RelayClientPool({ context, accountId, clock, random: options.random, fetch: options.fetch }, registry);
        const client = new sdk.MeshlineClient({ context, accountId, store, relayClients: pool, accountSigner: signer, secretProtector: protector, clock, random: options.random });
        let disposed = false; const close = async () => { if (disposed) return; disposed = true; await client.dispose(); await pool.dispose(); await store.dispose(); key.fill(0); };
        resources.push({ dispose: close }); await client.initialize();
        const account = client.accountManager; const device = client.deviceManager;
        if (!device.local) {
            const route = await account.getRoute(); const previous = route && await device.getOwnDeviceState(fixture.relayId);
            const local = await device.createDevice(86400); if (!route) await account.publishRoute(fixture.relayId, { validitySeconds: 86400 });
            await device.publishDeviceState(fixture.relayId, { certificates: [...previous?.certificates ?? [], local] });
        }
        const groups = client.groupManager; const messages = client.messageManager;
        for (const component of [groups, messages]) component.onLifecycle('backgroundError', value => {
            const error = value.error instanceof Error ? value.error.stack ?? String(value.error) : String(value.error);
            const message = `${value.operation}: ${error}`; errors.push(message); options.onDiagnostic?.(message);
        });
        return { accountId, name, device, groups, messages, dispose: close };
    }
    async function remote(id: string, seed: number) {
        const result = await command<{ accountId: string }>({ operation: 'open', id, seed }); await command({ operation: 'authorize', id, relayId: fixture.relayId }); return result;
    }
    const readNet = (id: string, group: sdk.GroupRef) => command<GroupRead>({ operation: 'group-read', id, ...group });
    const readTs = (local: Awaited<ReturnType<typeof open>>, group: sdk.GroupRef) => all(local.groups.getMessages({ groupId: group.groupId }));
    async function until(phase: string, condition: () => Promise<boolean>) {
        const started = Date.now(); let advances = 0;
        for (;;) {
            const complete = await condition(); const elapsedMs = Date.now() - started;
            options.onWait?.({ phase, elapsedMs, advances, complete }); if (complete) return;
            if (elapsedMs >= 90000) throw new Error(`Cross-device account synchronization did not converge in ${elapsedMs} ms. Background errors: ${errors.join('; ')}`);
            // Native crypto and SQLite must finish real work between logical poll
            // advances. A fixed fast iteration budget cancels slow runtimes early.
            await new Promise(resolve => setTimeout(resolve, 1000)); clock.tick(); advances++;
            const result = await command<{ now: number }>({ operation: 'advance' }); check(clock.wall === result.now, 'Interop logical clocks diverged');
        }
    }
    async function admit(local: Awaited<ReturnType<typeof open>>, id: string, other: string, owner: 'typescript' | 'dotnet'): Promise<sdk.GroupRef> {
        let group: sdk.GroupRef;
        if (owner === 'typescript') {
            group = (await local.groups.createGroup(fixture.relayId, { name: 'Native SDK interoperability', memberCapacity: 20 })).ref;
            const invite = await local.groups.createInvite(group, { invitee: other, expiresAt: clock.wall + 3600 });
            await command({ operation: 'group-apply', id, relayId: fixture.relayId, document: sdk.groupInviteCodec.stringify(invite.document) }); await local.groups.approveApplications(group, [other]);
        } else {
            group = await command<sdk.GroupRef>({ operation: 'group-create', id, relayId: fixture.relayId, name: 'Native SDK interoperability' });
            const invite = await command<{ document: string }>({ operation: 'group-invite', id, ...group, invitee: local.accountId });
            await local.groups.applyToGroup({ group, document: sdk.groupInviteCodec.parse(invite.document) }); await command({ operation: 'group-approve', id, ...group, accounts: [local.accountId] });
        }
        await local.groups.getGroup(group); await readNet(id, group); return group;
    }
    try {
        for (const owner of ['typescript', 'dotnet'] as const) {
            const id = `pair-${owner}`; let local = await open(103); const other = await remote(id, 104); const group = await admit(local, id, other.accountId, owner);
            check((await all(local.groups.getMembers(group))).length === 2 && (await readNet(id, group)).members.length === 2, 'Admission membership differs'); pass(`${owner} owner admits the other SDK through public signed invitation APIs`);
            const first = await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'Hermes → .NET 😀' } }); const received = (await readNet(id, group)).messages.at(-1)!;
            check(received.messageId === first.messageId && received.text === 'Hermes → .NET 😀', 'Actual .NET did not decrypt the TypeScript message'); pass(`${owner} group: actual .NET decrypts the native TypeScript Unicode message`);
            const second = await command<{ messageId: string }>({ operation: 'group-send', id, ...group, text: '.NET → Hermes 🌿' }); await local.groups.getGroup(group);
            const native = (await readTs(local, group)).at(-1)!; check(native.messageId === second.messageId && native.body?.text === '.NET → Hermes 🌿', 'TypeScript did not decrypt actual .NET content'); pass(`${owner} group: native TypeScript decrypts the actual .NET Unicode message`);
            await command({ operation: 'group-nickname', id, ...group, nickname: '跨 SDK 昵称' }); await local.groups.getGroup(group);
            check((await all(local.groups.getMembers(group))).find(value => value.accountId === other.accountId)?.nickname === '跨 SDK 昵称', 'Nickname was not applied');
            await command({ operation: 'group-nickname', id, ...group, nickname: null }); await local.groups.getGroup(group);
            check((await all(local.groups.getMembers(group))).find(value => value.accountId === other.accountId)?.nickname === undefined, 'Nickname deletion was not applied'); pass(`${owner} group: encrypted nickname update and deletion agree`);
            const ownerId = owner === 'typescript' ? local.accountId : other.accountId; const previousKey = (await all(local.groups.getMembers(group))).find(value => value.accountId === ownerId)!.memberEncryptionPublicKey;
            if (owner === 'typescript') await local.groups.rotateSecret(group, { rotateOwnerMemberKey: true }); else await command({ operation: 'group-rotate', id, ...group, ownerKey: true });
            await local.groups.getGroup(group); await readNet(id, group);
            check(!sdk.equalBytes(previousKey, (await all(local.groups.getMembers(group))).find(value => value.accountId === ownerId)!.memberEncryptionPublicKey), 'Owner member key did not rotate');
            await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'native after rotation' } }); await command({ operation: 'group-send', id, ...group, text: '.NET after rotation' }); await local.groups.getGroup(group);
            const messages = (await readTs(local, group)).map(value => value.body?.text); check(messages.length === 4 && same(messages, (await readNet(id, group)).messages.map(value => value.text)), 'Old/new epoch histories differ'); pass(`${owner} owner key rotates and both SDKs retain identical old and new epoch messages`);
            const deviceId = sdk.certificateId(local.device.certificate, context); const name = local.name; await local.dispose(); local = await open(103, name);
            check(sdk.certificateId(local.device.certificate, context) === deviceId && same((await readTs(local, group)).map(value => value.body?.text), messages), 'Native SQLite reopen lost identity or history'); pass(`${owner} group: native SQLite reopen preserves protected identity and both epochs`);
            await local.dispose(); await command({ operation: 'close', id });
        }
        {
            const id = 'from-dotnet'; const other = await remote(id, 109);
            const group = await command<sdk.GroupRef>({ operation: 'group-create', id, relayId: fixture.relayId, name: 'Account history from .NET' });
            await command({ operation: 'group-send', id, ...group, text: 'old .NET epoch' }); await command({ operation: 'group-rotate', id, ...group, ownerKey: true }); await command({ operation: 'group-send', id, ...group, text: 'current .NET epoch' });
            let local = await open(109); check(local.accountId === other.accountId && (await local.device.getOwnDeviceState(fixture.relayId))?.certificates.length === 2, 'Fresh native device replaced the other authorized device'); pass('new native device joins the same account without revoking its .NET device');
            await local.messages.start(); await local.groups.start(); await command({ operation: 'message-start', id }); await command({ operation: 'group-start', id });
            await until('native-history', async () => (await readTs(local, group)).length >= 2); check(same((await readTs(local, group)).map(value => value.body?.text), ['old .NET epoch', 'current .NET epoch']), 'Native device did not restore both .NET epochs'); pass('new native device restores old .NET history after owner-key replacement through account synchronization');
            const sent = await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'restored native sends' } }); check((await readNet(id, group)).messages.at(-1)?.messageId === sent.messageId, 'Restored native device cannot send'); pass('restored native device sends with recovered current group material');
            const name = local.name; await local.dispose(); await command({ operation: 'close', id }); local = await open(109, name);
            check((await readTs(local, group)).length === 3, 'Account-restored native history did not survive reopen'); pass('native SQLite reopen retains account-restored old and current history'); await local.dispose();
        }
        {
            const id = 'from-typescript'; let local = await open(110); const group = (await local.groups.createGroup(fixture.relayId, { name: 'Account history from native', memberCapacity: 20 })).ref;
            await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'old native epoch' } }); await local.groups.rotateSecret(group, { rotateOwnerMemberKey: true }); await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'current native epoch' } });
            const other = await remote(id, 110); check(other.accountId === local.accountId && (await local.device.getOwnDeviceState(fixture.relayId))?.certificates.length === 2, 'Fresh .NET device replaced the native device'); pass('new .NET device joins the same account without revoking its native device');
            await local.messages.start(); await local.groups.start(); await command({ operation: 'message-start', id }); await command({ operation: 'group-start', id });
            await until('dotnet-history', async () => (await readNet(id, group)).messages.length >= 2); check(same((await readNet(id, group)).messages.map(value => value.text), ['old native epoch', 'current native epoch']), '.NET did not restore both native epochs'); pass('new .NET device restores native old history after owner-key replacement through account synchronization');
            const sent = await command<{ messageId: string }>({ operation: 'group-send', id, ...group, text: 'restored .NET sends' }); await local.groups.getGroup(group); check((await readTs(local, group)).at(-1)?.messageId === sent.messageId, 'Native client cannot read the restored .NET device message'); pass('native SDK decrypts the restored .NET device message');
            const name = local.name; await local.dispose(); await command({ operation: 'close', id }); local = await open(110, name); check((await readTs(local, group)).length === 3, 'Native owner lost history after synchronization'); pass('native owner reopens its protected history after synchronizing to .NET'); await local.dispose();
        }
        for (const owner of ['typescript', 'dotnet'] as const) {
            const id = `recovery-${owner}`; let local = await open(111); const other = await remote(id, 112); const group = await admit(local, id, other.accountId, owner);
            await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'history before key loss' } }); check((await readNet(id, group)).messages.at(-1)?.text === 'history before key loss', 'Original member did not decrypt history'); pass(`${owner} recovery: original member reads the old epoch before losing its private key`);
            if (owner === 'typescript') {
                await command({ operation: 'close', id }); await remote(id, 112); check((await readNet(id, group)).messages.length === 0, 'Fresh .NET device recovered old keys without synchronization'); pass('fresh .NET member device has no old group history before explicit key recovery');
                await command({ operation: 'group-recovery', id, ...group }); await local.groups.approveKeyRecovery(group, [other.accountId]); await readNet(id, group);
                const sent = await command<{ messageId: string }>({ operation: 'group-send', id, ...group, text: 'new .NET member key' }); await local.groups.getGroup(group);
                check(same((await readNet(id, group)).messages.map(value => value.text), ['new .NET member key']) && (await readTs(local, group)).at(-1)?.messageId === sent.messageId, 'Native approval granted old history or failed new-key exchange'); pass('native owner approves .NET member recovery without granting old history');
            } else {
                await local.dispose(); local = await open(111); await local.groups.getGroup(group); check((await readTs(local, group)).length === 0, 'Fresh native device recovered old keys without synchronization'); pass('fresh native member device has no old group history before explicit key recovery');
                await local.groups.requestKeyRecovery(group); await command({ operation: 'group-recovery-approve', id, ...group, accounts: [local.accountId] }); await local.groups.getGroup(group);
                const sent = await local.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'new native member key' } });
                check(same((await readTs(local, group)).map(value => value.body?.text), ['new native member key']) && (await readNet(id, group)).messages.at(-1)?.messageId === sent.messageId, '.NET approval granted old history or failed native new-key exchange'); pass('.NET owner approves native member recovery without granting old history');
            }
            await local.dispose(); await command({ operation: 'close', id });
        }
        check(errors.length === 0, `Unexpected native background errors: ${errors.join('; ')}`);
        const evidence = await command<InteropSnapshot>({ operation: 'finish' }); await onEvidence?.(evidence);
        check(evidence.closed && evidence.errors.length === 0 && evidence.databases.length === 7 && evidence.databases.every(value => value.size > 0)
            && ['typescript', 'dotnet'].every(actor => evidence.requests.some(value => value.actor === actor && value.method === 'group.secret.rotation.commit')), 'Independent peer or .NET persistence evidence is incomplete');
        pass('actual .NET process closes seven independent SQLite sessions with no relay validation or native background errors');
    } catch (error) { onResult({ name: 'native .NET group interoperability', passed: false, error: error instanceof Error ? error.stack ?? String(error) : String(error) }); throw error; }
    finally {
        for (const resource of resources.reverse()) await resource.dispose();
        await command({ operation: 'finish' }); await onEvidence?.(await json<InteropSnapshot>(`/interop/${run}/observations`));
    }
}
