import * as sdk from '@meshline/sdk';
import { context } from './relay-fixture.js';
import type { ReconnectEvent, ReconnectSnapshot } from './reconnect-models.js';

export interface ReconnectOptions {
    readonly origin: string; readonly fetch: sdk.RelayFetch; readonly socketFactory: sdk.RelaySocketFactory;
    readonly random: sdk.RandomSource; createStore(): sdk.MeshlineStore;
}
export interface ReconnectResult { readonly name: string; readonly passed: boolean; readonly error?: string }
export interface ReconnectEvidence { readonly run: string; readonly server: ReconnectSnapshot; readonly backgroundErrors: readonly string[]; readonly subscriptionErrors: readonly string[] }
function check(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
const delay = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds));
async function bounded<T>(promise: Promise<T>, milliseconds = 15000): Promise<T> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try { return await Promise.race([promise, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Reconnect acceptance deadline exceeded.')), milliseconds); })]); }
    finally { clearTimeout(timer); }
}
const methods = (snapshot: ReconnectSnapshot, kind: string, method: string, connection?: number) => snapshot.events.filter(value => value.kind === kind && value.method === method && (connection === undefined || value.connection === connection));
const errorText = (error: unknown) => error instanceof Error ? `${error.name}${error instanceof sdk.ProtocolError || error instanceof sdk.RelayError ? `/${error.code}` : ''}: ${error.message}` : String(error);

/** Both the Node TLS regression and installed Android consumer run this scenario. */
export async function runReconnectChecks(options: ReconnectOptions, onResult: (result: ReconnectResult) => void, onEvidence?: (evidence: ReconnectEvidence) => void): Promise<void> {
    const origin = new URL(options.origin); check(origin.protocol === 'https:' && origin.hostname === '127.0.0.1', 'Reconnect acceptance requires the local TLS fixture.');
    const run = [...options.random.bytes(12)].map(value => value.toString(16).padStart(2, '0')).join('');
    async function fetchSnapshot(path: string, body?: sdk.JsonObject): Promise<ReconnectSnapshot> {
        const scope = sdk.abortScope([], 5000);
        try {
            const response = await options.fetch(`${options.origin}/reconnect/${path}`, { method: body ? 'POST' : 'GET', headers: { ...(body ? { 'Content-Type': 'application/json' } : {}) },
                ...(body ? { body: JSON.stringify(body) } : {}), signal: scope.signal, credentials: 'omit', redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer' });
            const text = sdk.decodeUtf8(new Uint8Array(await response.arrayBuffer())); check(response.status === 200, `Reconnect fixture returned ${response.status}: ${text}`); return JSON.parse(text) as ReconnectSnapshot;
        } finally { scope.dispose(); }
    }
    const fixture = await fetchSnapshot('begin', { run });
    const observe = () => fetchSnapshot(`${run}/observations`);
    const control = (action: string) => fetchSnapshot(`${run}/control`, { action });
    async function untilServer(predicate: (snapshot: ReconnectSnapshot) => boolean, milliseconds = 12000): Promise<ReconnectSnapshot> {
        const until = Date.now() + milliseconds;
        do { const snapshot = await observe(); check(snapshot.errors.length === 0, snapshot.errors.join('; ')); if (predicate(snapshot)) return snapshot; await delay(50); } while (Date.now() < until);
        throw new Error('Required reconnect server observation did not arrive.');
    }
    const accountKey = new Uint8Array(32).fill(31); const publicKey = sdk.accountPublicKey(accountKey); const accountId = sdk.getAccountId('neo:860833102', publicKey);
    const signer: sdk.AccountSigner = { accountId, publicKey, sign: async input => sdk.signAccount(input, accountKey, options.random) };
    const protector: sdk.SecretProtector = { async protect(bytes, purpose) { const nonce = options.random.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(accountKey, nonce, bytes, sdk.encodeUtf8(purpose))); }, async unprotect(bytes, purpose) { return sdk.decryptAes(accountKey, bytes.slice(0, 12), bytes.slice(12), sdk.encodeUtf8(purpose)); } };
    const registry: sdk.RelayRegistry = { context, async getRelay(id) { return id === fixture.relayId ? { relayId: id, endpoint: fixture.endpoint, status: 'active', updatedAt: BigInt(Date.now()) } : undefined; }, async *getRelays() { yield (await this.getRelay(fixture.relayId))!; } };
    const backgroundErrors: unknown[] = []; const subscriptionErrors: unknown[] = [];
    async function open(migrate: boolean) {
        const store = options.createStore(); if (migrate) await store.migrate();
        const pool = new sdk.RelayClientPool({ context, accountId, fetch: options.fetch, socketFactory: options.socketFactory, random: options.random, requestTimeoutMilliseconds: 10000 }, registry);
        const account = new sdk.AccountManager({ context, accountId, store, relayClients: pool, accountSigner: signer }); await account.initialize();
        const device = new sdk.DeviceManager({ context, accountId, store, relayClients: pool, accountManager: account, accountSigner: signer, secretProtector: protector, random: options.random }); await device.initialize();
        if (!device.local) await device.createDevice(3600);
        const channels = new sdk.ChannelManager({ context, accountId, store, relayClients: pool, deviceManager: device, random: options.random }); await channels.initialize();
        channels.onLifecycle('backgroundError', failure => { backgroundErrors.push(failure.error); });
        return { store, pool, account, device, channels, async dispose() { await channels.dispose(); await device.dispose(); await account.dispose(); await pool.dispose(); await store.dispose(); } };
    }
    const local = await open(true); let reopened: Awaited<ReturnType<typeof open>> | undefined;
    let subscription: sdk.RelaySubscription | undefined; let cleanupSubscription: sdk.RelaySubscription | undefined;
    const pass = (name: string) => onResult({ name, passed: true });
    const posts = async (manager = local.channels) => { const reader = await manager.getPosts({ channelId: fixture.channel.channelId }); try { return await reader.readNext(10); } finally { await reader.dispose(); } };
    async function untilPosts(count: number) { await bounded((async () => { while ((await posts()).length !== count) await delay(50); })()); }
    const ids = (event: ReconnectEvent) => event.params?.group_ids;
    try {
        const client = await local.pool.get(fixture.relayId, { mode: 'device', signer: local.device });
        check(client.state.authentication === 'device' && (await observe()).events.some(value => value.kind === 'authenticated' && value.connection === 0), 'HTTPS device proof was not validated.');
        pass('native HTTPS session uses a verified device authentication proof');
        await local.channels.follow(fixture.channel); await local.channels.start(); await untilPosts(1);
        let observed = await untilServer(value => methods(value, 'ack', 'channel.subscribe').length === 1);
        const firstConnection = methods(observed, 'ack', 'channel.subscribe')[0]!.connection!;
        check(observed.events.some(value => value.kind === 'authenticated' && value.connection === firstConnection), 'WSS session skipped device authentication.');
        check((await posts())[0]!.body?.text === '重连帖子 1 😀', 'Initial signed timeline did not project correctly.');
        pass('authenticated native WSS subscription receives its initial verified channel history');

        await control('drop-and-hold'); observed = await untilServer(value => value.heldAuthentication === 1);
        const secondConnection = observed.openConnections[0]!;
        check(secondConnection !== firstConnection && observed.events.some(value => value.kind === 'close' && value.connection === firstConnection)
            && !methods(observed, 'rpc', 'channel.subscribe', secondConnection).length, 'Reconnect sent a subscription before authentication or reused the closed connection.');
        pass('server disconnect closes the old native socket and reauthenticates before resubscribing');

        await control('append-silent'); check((await posts()).length === 1, 'A silent disconnected post appeared before transport recovery.');
        await control('release-auth'); await untilPosts(2);
        observed = await untilServer(value => value.heldSubscriptions === 1);
        check(methods(observed, 'ack', 'channel.subscribe', secondConnection).length === 0 && observed.events.filter(value => value.kind === 'notification').length === 0, 'The disconnected catch-up depended on a subscription ACK or notification.');
        pass('socket reconnection catches up a missed post without a notification');

        await control('append-silent'); await delay(150); check((await posts()).length === 2, 'Post added behind the delayed ACK was already applied.');
        await control('release-subscriptions'); await untilPosts(3); observed = await observe();
        const ack = methods(observed, 'ack', 'channel.subscribe', secondConnection)[0]!;
        check(methods(observed, 'http', 'channel.read').some(value => value.index > ack.index && Number(value.params?.after) === 2), 'Subscription ACK did not trigger cursor catch-up.');
        pass('delayed subscription acknowledgement triggers a second cursor catch-up');

        await control('append-notify'); await untilPosts(4); observed = await observe();
        check(observed.events.some(value => value.kind === 'notification' && value.sequence === 4) && (await posts()).every((value, index) => value.body?.text === `重连帖子 ${index + 1} 😀`), 'Live notification lost or duplicated verified content.');
        pass('native timeline notification projects Unicode posts once in accepted order');

        let acknowledged = 0;
        subscription = client.createSubscription('group.subscribe', { group_ids: [] }, { onSubscribed: () => { acknowledged++; }, onError: error => { subscriptionErrors.push(error); } });
        await control('malformed-group-ack'); subscription.update({ group_ids: [fixture.groups[0]!] });
        observed = await untilServer(value => value.heldAuthentication === 1 && value.events.some(row => row.kind === 'close' && row.connection === secondConnection));
        check(acknowledged === 0 && subscriptionErrors.some(error => error instanceof sdk.ProtocolError && error.code === 'invalid_response'), 'Malformed ACK was accepted or its diagnostic disappeared.');
        pass('malformed subscription ACK retires its connection and retains the protocol error');

        const thirdConnection = observed.openConnections[0]!;
        subscription.update({ group_ids: [fixture.groups[1]!] }); const desired = { group_ids: [fixture.groups[1]!, fixture.groups[2]!] }; subscription.update(desired); desired.group_ids.push(fixture.groups[0]!);
        await control('release-auth'); observed = await untilServer(value => methods(value, 'ack', 'group.subscribe', thirdConnection).length === 1 && methods(value, 'ack', 'channel.subscribe', thirdConnection).length === 1);
        check(methods(observed, 'rpc', 'group.subscribe', thirdConnection).length === 1 && JSON.stringify(ids(methods(observed, 'rpc', 'group.subscribe', thirdConnection)[0]!)) === JSON.stringify(fixture.groups.slice(1)), 'Reconnect did not coalesce or snapshot the latest desired set.');
        await bounded((async () => { while (Number(acknowledged) !== 1) await delay(10); })());
        pass('reconnection restores channel subscriptions and only the latest snapshotted group set');

        await control('reject-empty-group'); subscription.update({ group_ids: [] });
        observed = await untilServer(value => methods(value, 'ack', 'group.subscribe', thirdConnection).some(row => Array.isArray(ids(row)) && (ids(row) as sdk.JsonValue[]).length === 0));
        const emptyAttempts = methods(observed, 'rpc', 'group.subscribe', thirdConnection).filter(value => Array.isArray(ids(value)) && (ids(value) as sdk.JsonValue[]).length === 0);
        check(emptyAttempts.length === 2 && observed.events.some(value => value.kind === 'rejected-empty') && Number(acknowledged) === 1
            && subscriptionErrors.some(error => error instanceof sdk.RelayError && error.code === 'temporarily_unavailable'), 'Transient empty replacement did not retry once with a visible error.');
        pass('transient rejection retries the empty replacement without another application update');

        await control('hold-group-ack'); subscription.update({ group_ids: [fixture.groups[0]!] }); await untilServer(value => value.heldSubscriptions === 1);
        let disposed = false; const disposal = subscription.dispose().then(() => { disposed = true; }); await delay(100); check(!disposed, 'Disposal skipped the pending replacement.');
        await control('release-subscriptions'); await bounded(disposal, 8000); observed = await observe();
        const groupFrames = methods(observed, 'rpc', 'group.subscribe', thirdConnection);
        check(JSON.stringify(ids(groupFrames.at(-2)!)) === JSON.stringify([fixture.groups[0]]) && JSON.stringify(ids(groupFrames.at(-1)!)) === '[]', 'Pending replacement and final clear were reordered.');
        subscription = undefined; pass('subscription disposal drains an in-flight ACK before clearing the same connection');

        cleanupSubscription = client.createSubscription('group.subscribe', { group_ids: [] }, { onError: error => { subscriptionErrors.push(error); } });
        await control('hold-group-ack'); cleanupSubscription.update({ group_ids: [fixture.groups[1]!] }); await untilServer(value => value.heldSubscriptions === 1);
        const started = Date.now(); await bounded(cleanupSubscription.dispose(), 8000);
        check(Date.now() - started < 8000 && cleanupSubscription.lastError instanceof Error && cleanupSubscription.lastError.name === 'TimeoutError', 'Unknown replacement cleanup was not bounded or lost its timeout diagnostic.');
        observed = await untilServer(value => value.events.some(row => row.kind === 'close' && row.connection === thirdConnection) && methods(value, 'ack', 'channel.subscribe').some(row => row.connection !== firstConnection && row.connection !== secondConnection && row.connection !== thirdConnection));
        cleanupSubscription = undefined; pass('lost native subscription ACK has bounded cleanup and retires only its old connection');

        const lastConnection = observed.openConnections[0]!; await bounded(local.channels.stop(), 8000);
        observed = await untilServer(value => methods(value, 'ack', 'channel.subscribe', lastConnection).some(row => JSON.stringify(row.params?.channel_ids) === '[]'));
        check(observed.openConnections.includes(lastConnection) && local.channels.lifecycleState === 'stopped', 'Channel stop failed to clear subscriptions or disposed its shared relay.');
        pass('channel manager stop sends the final empty set and preserves its caller-owned relay');

        const deviceId = sdk.certificateId(local.device.certificate, context); await local.dispose(); await untilServer(value => value.openConnections.length === 0);
        reopened = await open(false); const saved = await posts(reopened.channels);
        check(saved.length === 4 && saved.at(-1)!.body?.text === '重连帖子 4 😀' && sdk.certificateId(reopened.device.certificate, context) === deviceId, 'SQLite reopen lost the verified catch-up or protected identity.');
        pass('verified reconnect history and protected device identity survive closing native SQLite');

        observed = await observe(); check(observed.errors.length === 0, observed.errors.join('; '));
        check(backgroundErrors.every(error => error instanceof sdk.ProtocolError && ['socket_closed', 'socket_error'].includes(error.code)), `Unexpected channel background error: ${backgroundErrors.map(errorText).join('; ')}`);
        pass('server validations succeed and only injected disconnect errors reach background diagnostics');
    } catch (error) { onResult({ name: 'native reconnect workflow', passed: false, error: error instanceof Error ? error.stack ?? String(error) : String(error) }); throw error; }
    finally {
        await subscription?.dispose(); await cleanupSubscription?.dispose(); await reopened?.dispose(); await local.dispose(); accountKey.fill(0);
        onEvidence?.({ run, server: await observe(), backgroundErrors: backgroundErrors.map(errorText), subscriptionErrors: subscriptionErrors.map(errorText) });
    }
}
