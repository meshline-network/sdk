import * as sdk from '@meshline/sdk';
import { MessagingClock } from './clock.js';
import type { InteropFixture, InteropSnapshot } from './interop-models.js';
import type { InteropOptions, InteropResult } from './interop.js';

export interface ProcessRecoveryState {
    readonly fixture: InteropFixture; readonly database: string; readonly now: number;
    readonly accountId: string; readonly remoteAccount: string; readonly deviceId: string; readonly group: sdk.GroupRef;
    readonly messageId: string; readonly oldOwnerKey: string; readonly candidateOwnerKey: string;
    readonly channel: sdk.ChannelRef; readonly channelMessageId: string; readonly channelSequence: number;
    readonly persisted: readonly { readonly query: sdk.RecordKey; readonly sha256: string }[];
    readonly members: readonly ProcessRecoveryMemberState[];
}
export interface ProcessRecoveryMemberState {
    readonly kind: 'application' | 'recovery'; readonly seed: 119 | 120; readonly database: string;
    readonly accountId: string; readonly deviceId: string; readonly group: sdk.GroupRef;
    readonly candidatePublicKey: string; readonly protectedKeySha256: string; readonly oldMemberKey?: string;
    readonly persisted: readonly { readonly query: sdk.RecordKey; readonly sha256: string }[];
}
const check = (value: unknown, message: string): void => { if (!value) throw new Error(message); };
const digest = (value: sdk.JsonValue) => [...sdk.sha256(sdk.encodeUtf8(sdk.canonicalJson(value)))].map(value => value.toString(16).padStart(2, '0')).join('');
const delay = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
async function all<T>(query: Promise<sdk.QueryReader<T>>): Promise<T[]> {
    const reader = await query; try { const rows: T[] = []; for (;;) { const page = await reader.readNext(100); if (!page.length) return rows; rows.push(...page); } } finally { await reader.dispose(); }
}
function control(options: InteropOptions) {
    const origin = new URL(options.origin); check(origin.protocol === 'https:' && origin.hostname === '127.0.0.1', 'Only the loopback TLS acceptance peer is allowed');
    return async <T>(path: string, value?: unknown): Promise<T> => {
        const scope = sdk.abortScope([], 60000);
        try {
            const response = await options.fetch(new URL(path, origin).href, { method: value === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json' },
                ...(value === undefined ? {} : { body: JSON.stringify(value) }), signal: scope.signal, redirect: 'error', credentials: 'omit', cache: 'no-store', referrerPolicy: 'no-referrer' });
            const text = sdk.decodeUtf8(new Uint8Array(await response.arrayBuffer())); check(response.status === 200, `Recovery peer ${response.status}: ${text}`); return JSON.parse(text) as T;
        } finally { scope.dispose(); }
    };
}
async function open(options: InteropOptions, fixture: InteropFixture, database: string, now: number, beforeInitialize?: (store: sdk.MeshlineStore) => Promise<void>, identity: { seed?: number; clock?: MessagingClock } = {}) {
    const clock = identity.clock ?? new MessagingClock(); if (!identity.clock) clock.wall = now;
    const context = sdk.NetworkContext.parse(fixture.context); const key = new Uint8Array(32).fill(identity.seed ?? 117); const publicKey = sdk.accountPublicKey(key); const accountId = sdk.getAccountId('neo:860833102', publicKey);
    const store = options.createStore(database); await store.migrate(); await store.initialize({ context: fixture.context, accountId });
    const entry: sdk.RelayEntry = { relayId: fixture.relayId, endpoint: fixture.endpoint, status: 'active', updatedAt: BigInt(now) * 1000n };
    const registry: sdk.RelayRegistry = { context, getRelay: async id => id === entry.relayId ? entry : undefined, getRelays: () => (async function* () { yield entry; })() };
    const signer: sdk.AccountSigner = { accountId, publicKey, sign: async input => sdk.signAccount(input, key, options.random) };
    const protector: sdk.SecretProtector = { protect: async (bytes, purpose) => { const nonce = options.random.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(key, nonce, bytes, sdk.encodeUtf8(purpose))); },
        unprotect: async (bytes, purpose) => sdk.decryptAes(key, bytes.slice(0, 12), bytes.slice(12), sdk.encodeUtf8(purpose)) };
    // Leave a ten-minute window for external process termination. The test does
    // not simulate a crash by aborting requests or gracefully closing the store.
    const pool = new sdk.RelayClientPool({ context, accountId, clock, random: options.random, fetch: options.fetch, requestTimeoutMilliseconds: 600000 }, registry);
    const client = new sdk.MeshlineClient({ context, accountId, store, relayClients: pool, accountSigner: signer, secretProtector: protector, clock, random: options.random });
    const errors: string[] = []; let disposed = false;
    const dispose = async () => { if (disposed) return; disposed = true; await client.dispose(); await pool.dispose(); await store.dispose(); key.fill(0); };
    for (const [name, component] of [['messages', client.messageManager], ['groups', client.groupManager], ['channels', client.channelManager]] as const) {
        component.onLifecycle('backgroundError', value => {
            const detail = value.error instanceof Error ? value.error.stack ?? String(value.error) : String(value.error);
            const message = `[${database}; ${accountId}; ${name}] ${value.operation}: ${detail}`;
            errors.push(message); options.onDiagnostic?.(message);
        });
    }
    try {
        await beforeInitialize?.(store); await client.initialize();
        if (!client.deviceManager.local) { const certificate = await client.deviceManager.createDevice(86400); await client.accountManager.publishRoute(fixture.relayId, { validitySeconds: 86400 }); await client.deviceManager.publishDeviceState(fixture.relayId, { certificates: [certificate] }); }
        return { client, store, context, accountId, clock, errors, dispose };
    } catch (error) { await dispose(); throw error; }
}
function helpers(options: InteropOptions, fixture: InteropFixture, clock: MessagingClock) {
    const json = control(options);
    const command = async <T = unknown>(value: Record<string, unknown>): Promise<T> => (await json<{ result: T }>(`/interop/${fixture.run}/command`, value)).result;
    const observe = () => json<InteropSnapshot>(`/interop/${fixture.run}/observations`);
    const until = async (name: string, condition: () => Promise<boolean>, advance = true) => {
        const started = Date.now();
        for (;;) {
            if (await condition()) return;
            if (Date.now() - started > 90000) throw new Error(`Recovery deadline: ${name}`);
            await delay(1000);
            if (advance) { clock.tick(); const result = await command<{ now: number }>({ operation: 'advance' }); check(clock.wall === result.now, 'Recovery clocks diverged'); }
        }
    };
    return { command, observe, until };
}

/** Returns while five accepted HTTPS acknowledgements are still withheld. The caller
 * must retain the resources and terminate this process externally. */
export async function prepareProcessRecovery(options: InteropOptions, onResult: (value: InteropResult) => void) {
    const run = [...options.random.bytes(12)].map(value => value.toString(16).padStart(2, '0')).join('');
    const fixture = await control(options)<InteropFixture>('/interop/begin', { run }); const database = `meshline-process-${run}.sqlite`;
    const local = await open(options, fixture, database, fixture.now); const { client, store, clock } = local; const messages = client.messageManager; const groups = client.groupManager; const channels = client.channelManager;
    const { command, observe, until } = helpers(options, fixture, clock); const pass = (name: string) => onResult({ name, passed: true });
    const memberLocals: Awaited<ReturnType<typeof open>>[] = [];
    const setups: { kind: 'application' | 'recovery'; seed: 119 | 120; database: string; local: Awaited<ReturnType<typeof open>>; invitation: sdk.GroupInvitation; oldMemberKey?: string }[] = [];
    const interruption = new AbortController(); const pendingOperations: Promise<unknown>[] = [];
    const withhold = (operation: Promise<unknown>, name: string) => {
        const pending = operation.then(() => { throw new Error(`Withheld ${name} unexpectedly completed`); }); pendingOperations.push(pending);
        void pending.catch(error => { if (!interruption.signal.aborted) { local.errors.push(String(error)); options.onDiagnostic?.(String(error)); } });
    };
    const dispose = async () => { interruption.abort(); await Promise.allSettled(pendingOperations); for (const member of memberLocals) await member.dispose(); await local.dispose(); await command({ operation: 'finish' }); };
    try {
        const remote = await command<{ accountId: string }>({ operation: 'open', id: 'recovery', seed: 118 }); await command({ operation: 'authorize', id: 'recovery', relayId: fixture.relayId });
        await messages.start(); await command({ operation: 'message-start', id: 'recovery' }); await command({ operation: 'contact-add', id: 'recovery', account: local.accountId });
        await until('incoming contact consent', async () => (await all(messages.getContactRequests({ direction: 'incoming' }))).some(value => value.accountId === remote.accountId));
        await messages.acceptContactRequest(remote.accountId); await until('mutual contact', async () => (await command<{ present: boolean }>({ operation: 'contact-read', id: 'recovery', account: local.accountId })).present);
        await messages.stop(); pass('public native and .NET contact consent establishes the direct-message grant');
        const group = (await groups.createGroup(fixture.relayId, { name: 'Process recovery', memberCapacity: 20 })).ref;
        const invite = await groups.createInvite(group, { invitee: remote.accountId, expiresAt: clock.wall + 3600 });
        await command({ operation: 'group-apply', id: 'recovery', relayId: fixture.relayId, document: sdk.groupInviteCodec.stringify(invite.document) }); await groups.approveApplications(group, [remote.accountId]);
        await groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'old epoch before process termination' } });
        const initial = await command<{ messages: { text: string }[] }>({ operation: 'group-read', id: 'recovery', ...group }); check(initial.messages[0]?.text === 'old epoch before process termination', '.NET cannot decrypt the old epoch');
        const oldOwnerKey = sdk.encodeBase64Url((await all(groups.getMembers(group))).find(value => value.accountId === local.accountId)!.memberEncryptionPublicKey); pass('both SDKs retain verified old group history before the interrupted rotation');
        const channel = (await channels.createChannel(fixture.relayId, 'Process publication recovery', { moderators: [remote.accountId] })).ref;
        await channels.follow(channel); await channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'channel history before termination' } });
        await command({ operation: 'channel-follow', id: 'recovery', ...channel });
        const previousPosts = await command<{ posts: { text: string }[] }>({ operation: 'channel-read', id: 'recovery', ...channel });
        check(previousPosts.posts.length === 1 && previousPosts.posts[0]!.text === 'channel history before termination', '.NET channel history differs before interruption');
        pass('both SDKs retain verified channel history and followed state before publication interruption');
        for (const kind of ['application', 'recovery'] as const) {
            const seed = kind === 'application' ? 119 : 120; const memberDatabase = `meshline-process-${run}-${kind}.sqlite`;
            const member = await open(options, fixture, memberDatabase, clock.wall, undefined, { seed, clock }); memberLocals.push(member);
            const memberGroup = await command<sdk.GroupRef>({ operation: 'group-create', id: 'recovery', relayId: fixture.relayId, name: `Offline ${kind} approval` });
            const document = await command<{ document: string }>({ operation: 'group-invite', id: 'recovery', ...memberGroup, invitee: member.accountId });
            const invitation = { group: memberGroup, document: sdk.groupInviteCodec.parse(document.document) }; let oldMemberKey: string | undefined;
            if (kind === 'recovery') {
                await member.client.groupManager.applyToGroup(invitation);
                await command({ operation: 'group-approve', id: 'recovery', ...memberGroup, accounts: [member.accountId] });
                await command({ operation: 'group-send', id: 'recovery', ...memberGroup, text: 'member history before termination' });
                await member.client.groupManager.getGroup(memberGroup);
                oldMemberKey = sdk.encodeBase64Url((await all(member.client.groupManager.getMembers(memberGroup))).find(value => value.accountId === member.accountId)!.memberEncryptionPublicKey);
                check((await all(member.client.groupManager.getMessages())).some(value => value.body?.text === 'member history before termination'), 'Recovery member did not retain its original decrypted history');
            }
            setups.push({ kind, seed, database: memberDatabase, local: member, invitation, ...(oldMemberKey ? { oldMemberKey } : {}) });
        }
        pass('separate native stores retain targeted admission and verified prior recovery membership with the actual .NET owner');
        const sent = await messages.sendMessage(remote.accountId, { body: { contentType: 'text/plain', text: 'direct accepted before process termination 😀' } });
        await command({ operation: 'hold-recovery-responses', messageId: sent.messageId, groupId: group.groupId, channelId: channel.channelId,
            applicationGroupId: setups[0]!.invitation.group.groupId, recoveryGroupId: setups[1]!.invitation.group.groupId });
        withhold(groups.rotateSecret(group, { rotateOwnerMemberKey: true }, interruption.signal), 'rotation');
        withhold(channels.publishPost(channel, { body: { contentType: 'text/plain', text: 'channel accepted before process termination 😀' } }, interruption.signal), 'publication');
        for (const member of setups) withhold(member.kind === 'application'
            ? member.local.client.groupManager.applyToGroup(member.invitation, interruption.signal)
            : member.local.client.groupManager.requestKeyRecovery(member.invitation.group, interruption.signal), member.kind);
        await messages.start();
        await until('five accepted responses held', async () => { check(local.errors.length === 0, local.errors.join('; ')); return (await observe()).faults.length === 5; }, false);
        check((await messages.getSendStatus(sent.messageId))?.state === 'submitting', 'Outbox did not persist its in-flight submission'); pass('accepted direct message remains durably submitting while its HTTPS response is withheld');
        const rotationKey = { collection: 'group_rotations', key: group.groupId }; const candidate = (await store.read([rotationKey])).sets[0]![0]!.value;
        check(typeof candidate.protectedSecret === 'string' && typeof candidate.ownerPublicKey === 'string' && candidate.ownerPublicKey !== oldOwnerKey, 'Rotation lacks a protected new secret and owner key');
        const queries: sdk.RecordKey[] = [{ collection: 'local_device', key: 'current' }, { collection: 'message_outbox', key: sent.messageId },
            { collection: 'group_operations', key: `${group.groupId}|group.secret.rotation.commit` }, rotationKey, { collection: 'group_member_keys', key: `${group.groupId}|${candidate.ownerPublicKey}` },
            { collection: 'channel_operations', key: `${channel.channelId}|channel` }, { collection: 'channels', key: channel.channelId }];
        const pending = await store.read(queries); check(pending.sets.every(rows => rows.length === 1) && pending.sets[2]![0]!.value.accepted === undefined && typeof pending.sets[4]![0]!.value.protectedKey === 'string', 'Pending rotation or protected key was not persisted before sending');
        pass('unacknowledged rotation request and protected secret/owner-key candidate are persisted');
        const channelOperation = pending.sets[5]![0]!.value; const channelPost = sdk.channelPostCodec.decode(channelOperation.payload!);
        check(channelOperation.method === 'channel.post' && channelOperation.acceptedSequence === undefined && channelPost.channelId === channel.channelId
            && pending.sets[6]![0]!.value.isFollowed === true, 'Publication request or follow state is missing before termination');
        const acceptedPosts = await command<{ posts: { messageId: string; sequence: number; text: string }[] }>({ operation: 'channel-read', id: 'recovery', ...channel });
        const acceptedPost = acceptedPosts.posts.find(value => value.messageId === channelPost.messageId);
        check(acceptedPosts.posts.length === 2 && acceptedPost?.text === 'channel accepted before process termination 😀', '.NET cannot independently confirm the withheld publication');
        pass('the exact unacknowledged channel request persists while .NET independently observes its accepted post');
        const members: ProcessRecoveryMemberState[] = [];
        for (const member of setups) {
            const memberGroup = member.invitation.group; const method = member.kind === 'application' ? 'group.application.submit' : 'group.member.recovery.submit';
            const operationKey = { collection: 'group_operations', key: `${memberGroup.groupId}|${method}` };
            const operation = (await member.local.store.read([operationKey])).sets[0]![0]!.value;
            const request = member.kind === 'application' ? sdk.groupApplicationCodec.decode(operation.request!) : sdk.groupMemberRecoveryRequestCodec.decode(operation.request!);
            const publicKey = sdk.encodeBase64Url(request.memberEncryptionPublicKey);
            check(operation.accepted === undefined && request.account === member.local.accountId && request.groupId === memberGroup.groupId && publicKey !== member.oldMemberKey, 'Member submission or candidate changed before acknowledgement');
            const keys = [{ collection: 'local_device', key: 'current' }, operationKey, { collection: 'group_member_keys', key: `${memberGroup.groupId}|${publicKey}` }];
            const saved = await member.local.store.read(keys); check(saved.sets.every(rows => rows.length === 1) && typeof saved.sets[2]![0]!.value.protectedKey === 'string', 'Member identity, request or protected candidate was not persisted');
            members.push({ kind: member.kind, seed: member.seed, database: member.database, accountId: member.local.accountId, group: memberGroup,
                deviceId: sdk.certificateId(member.local.client.device!, local.context), candidatePublicKey: publicKey, protectedKeySha256: digest(saved.sets[2]![0]!.value.protectedKey!),
                ...(member.oldMemberKey ? { oldMemberKey: member.oldMemberKey } : {}), persisted: keys.map((query, index) => ({ query, sha256: digest(saved.sets[index]![0]!.value) })) });
            pass(`unacknowledged ${member.kind} request and its original protected candidate persist in an independent native store`);
        }
        const snapshot = await observe(); check(snapshot.faults.every(value => value.disconnectedAt === undefined) && !snapshot.offlineApprovals.length
            && snapshot.errors.length === 0 && local.errors.length === 0 && memberLocals.every(value => value.errors.length === 0), 'Connections closed, approvals happened or the workflow failed before external termination');
        pass('independent peer confirms all five accepted acknowledgements are withheld on live connections before any offline approval');
        const state: ProcessRecoveryState = { fixture, database, now: clock.wall, accountId: local.accountId, remoteAccount: remote.accountId, group, messageId: sent.messageId,
            deviceId: sdk.certificateId(client.device!, local.context), oldOwnerKey, candidateOwnerKey: String(candidate.ownerPublicKey), channel, channelMessageId: channelPost.messageId, channelSequence: acceptedPost!.sequence,
            persisted: queries.map((query, index) => ({ query, sha256: digest(pending.sets[index]![0]!.value) })), members };
        pass('ready for external process termination without SDK disposal or SQLite close');
        return { state, snapshot, dispose };
    } catch (error) { await dispose(); throw error; }
}

/** Runs in a new native/Node process using only persisted state and the same peer. */
export async function verifyProcessRecovery(options: InteropOptions, state: ProcessRecoveryState, onResult: (value: InteropResult) => void, onEvidence: (value: InteropSnapshot) => void | Promise<void>) {
    check(/^[a-f0-9]{24}$/.test(state.fixture.run) && state.database === `meshline-process-${state.fixture.run}.sqlite`, 'Invalid process recovery state');
    check(state.members.map(value => value.kind).join(',') === 'application,recovery' && state.members.every(value => value.seed === (value.kind === 'application' ? 119 : 120)
        && value.database === `meshline-process-${state.fixture.run}-${value.kind}.sqlite` && value.persisted.length === 3), 'Invalid member process recovery state');
    const pass = (name: string) => onResult({ name, passed: true });
    const before = await control(options)<InteropSnapshot>(`/interop/${state.fixture.run}/observations`);
    check(before.faults.length === 5 && before.faults.every(value => value.disconnectedAt !== undefined) && before.errors.length === 0 && !before.closed, 'Original process connections are not closed'); pass('the peer observed all five withheld connections close before recovery began');
    const terminatedAt = Math.max(...before.faults.map(value => value.disconnectedAt!));
    check(before.offlineApprovals.length === 2 && state.members.every(member => before.offlineApprovals.some(value => value.method === (member.kind === 'application' ? 'group.application.approve' : 'group.member.recovery.approve')
        && value.groupId === member.group.groupId && value.account === member.accountId && value.memberPublicKey === member.candidatePublicKey && value.approvedAt >= terminatedAt)), 'Matching .NET approvals did not happen after the original process connections closed');
    pass('actual .NET owner approved both original candidates only after all terminated connections closed');
    const local = await open(options, state.fixture, state.database, state.now, async store => {
        const records = await store.read(state.persisted.map(value => value.query)); check(records.sets.every((rows, index) => rows.length === 1 && digest(rows[0]!.value) === state.persisted[index]!.sha256), 'Persisted in-flight requests or encrypted keys changed before recovery');
        pass('SQLite restores the exact device, outbox, rotation, candidate and channel records');
    });
    const { client, store, clock } = local; const messages = client.messageManager; const groups = client.groupManager; const channels = client.channelManager;
    const { command, observe, until } = helpers(options, state.fixture, clock); const group = state.group;
    const members: { state: ProcessRecoveryMemberState; local: Awaited<ReturnType<typeof open>> }[] = [];
    try {
        for (const member of state.members) {
            const reopened = await open(options, state.fixture, member.database, state.now, async memberStore => {
                const saved = await memberStore.read(member.persisted.map(value => value.query));
                check(saved.sets.every((rows, index) => rows.length === 1 && digest(rows[0]!.value) === member.persisted[index]!.sha256), `${member.kind} records changed before recovery initialization`);
                pass(`SQLite restores the exact ${member.kind} identity, signed request and protected candidate before initialization`);
            }, { seed: member.seed, clock }); members.push({ state: member, local: reopened });
            const proof = sdk.encodeUtf8(`recovered ${member.kind} signing identity`);
            check(reopened.accountId === member.accountId && sdk.certificateId(reopened.client.device!, local.context) === member.deviceId
                && sdk.verifyDevice(proof, await reopened.client.deviceManager.sign(proof), reopened.client.device!.signingPublicKey), `${member.kind} signing identity changed`);
            pass(`initialization restores the original ${member.kind} signing identity`);
        }
        const proof = sdk.encodeUtf8('process recovered signing identity');
        check(sdk.certificateId(client.device!, local.context) === state.deviceId && sdk.verifyDevice(proof, await client.deviceManager.sign(proof), client.device!.signingPublicKey)
            && (await messages.getSendStatus(state.messageId))?.state === 'submissionUnknown', 'Initialization lost identity or did not recover submitting state'); pass('initialization restores the signing identity and marks the interrupted submission unknown');
        check(await messages.getContact(state.remoteAccount) && (await all(messages.getMessageHistory(state.remoteAccount))).some(value => value.key.messageId === state.messageId && value.body?.text === 'direct accepted before process termination 😀'), 'Contact grant or local direct history was lost'); pass('contact authorization and locally queued plaintext history survive process termination');
        check((await all(channels.getFollowed())).some(value => value.ref.channelId === state.channel.channelId) && (await all(channels.getPosts({ channelId: state.channel.channelId }))).some(value => value.body?.text === 'channel history before termination'), 'Followed channel or previously accepted history was lost');
        pass('followed channel and previously accepted local posts survive process termination');
        await messages.start(); await groups.start(); await channels.start();
        for (const member of members) await member.local.client.groupManager.start();
        await until('outbox, rotation, publication and member request reconciliation', async () => (await messages.getSendStatus(state.messageId))?.state === 'targetAccepted'
            && (await store.read([{ collection: 'group_operations' }, { collection: 'group_rotations' }, { collection: 'channel_operations' }])).sets.every(rows => rows.length === 0)
            && (await Promise.all(members.map(async member => (await member.local.store.read([{ collection: 'group_operations' }])).sets[0]!.length))).every(count => count === 0));
        pass('runtime recovery retains confirmed send status and clears rotation, publication and both member submissions without application resubmission');
        for (const member of members) {
            const approval = before.offlineApprovals.find(value => value.groupId === member.state.group.groupId)!; const manager = member.local.client.groupManager;
            await until(`${member.state.kind} approved welcome`, async () => (await all(manager.getMessages())).some(value => value.messageId === approval.welcomeMessageId
                && value.body?.text === (member.state.kind === 'application' ? '.NET approved admission while native process was absent' : '.NET approved recovery while native process was absent')));
            const publicKey = sdk.encodeBase64Url((await all(manager.getMembers(member.state.group))).find(value => value.accountId === member.state.accountId)!.memberEncryptionPublicKey);
            const protectedKey = (await member.local.store.read([member.state.persisted[2]!.query])).sets[0]![0]!.value.protectedKey!;
            check(publicKey === member.state.candidatePublicKey && digest(protectedKey) === member.state.protectedKeySha256
                && (member.state.kind !== 'recovery' || (await all(manager.getMessages())).some(value => value.body?.text === 'member history before termination')), `${member.state.kind} did not retain the approved original candidate and history`);
            pass(`verified ${member.state.kind} approval retains the original protected candidate and decrypts the offline .NET welcome`);
            const text = `native ${member.state.kind} replies after process restart`; const sent = await manager.sendMessage(member.state.group, { body: { contentType: 'text/plain', text } });
            const received = await command<{ messages: { messageId: string; text: string }[] }>({ operation: 'group-read', id: 'recovery', ...member.state.group });
            check(received.messages.filter(value => value.messageId === sent.messageId && value.text === text).length === 1, `.NET cannot decrypt the recovered ${member.state.kind} member reply`);
            pass(`actual .NET owner decrypts the recovered ${member.state.kind} member reply exactly once`);
        }
        const ownerKey = sdk.encodeBase64Url((await all(groups.getMembers(group))).find(value => value.accountId === state.accountId)!.memberEncryptionPublicKey);
        check(ownerKey === state.candidateOwnerKey && ownerKey !== state.oldOwnerKey, 'Recovery replaced or lost the protected owner-key candidate'); pass('verified rotation activates the original protected owner-key candidate');
        const sent = await groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'native new epoch after process restart' } });
        const netGroup = await command<{ messages: { messageId: string; text: string }[] }>({ operation: 'group-read', id: 'recovery', ...group });
        check(netGroup.messages.length === 2 && netGroup.messages[0]?.text === 'old epoch before process termination' && netGroup.messages[1]?.messageId === sent.messageId && netGroup.messages[1]?.text === 'native new epoch after process restart', '.NET lost old history or cannot decrypt recovered native keys'); pass('.NET decrypts old history and the new native epoch after interrupted owner-key rotation');
        const reply = await command<{ messageId: string }>({ operation: 'group-send', id: 'recovery', ...group, text: '.NET new epoch after native restart' }); await groups.getGroup(group);
        const nativeGroup = await all(groups.getMessages({ groupId: group.groupId })); check(nativeGroup.length === 3 && nativeGroup.at(-1)?.messageId === reply.messageId && nativeGroup.at(-1)?.body?.text === '.NET new epoch after native restart', 'Recovered native group cannot decrypt the .NET reply'); pass('native protected old/current epoch history also decrypts the .NET reply');
        await until('unique direct message on .NET', async () => (await command<{ messages: { messageId: string }[] }>({ operation: 'message-read', id: 'recovery' })).messages.some(value => value.messageId === state.messageId));
        const remoteHistory = await command<{ messages: { messageId: string; text: string }[] }>({ operation: 'message-read', id: 'recovery' }); check(remoteHistory.messages.filter(value => value.messageId === state.messageId && value.text === 'direct accepted before process termination 😀').length === 1, '.NET direct history differs or duplicates delivery'); pass('.NET retains exactly one decrypted direct message for the interrupted submission');
        const directReply = await command<{ messageId: string }>({ operation: 'message-send', id: 'recovery', account: state.accountId, text: '.NET direct reply after native restart' });
        await until('direct reply on native', async () => (await all(messages.getMessageHistory(state.remoteAccount))).some(value => value.key.messageId === directReply.messageId && value.body?.text === '.NET direct reply after native restart')); pass('recovered native contact keys decrypt a fresh .NET direct reply');
        const recoveredPosts = await all(channels.getPosts({ channelId: state.channel.channelId }));
        const dotnetPosts = await command<{ posts: { messageId: string; sequence: number; text: string }[] }>({ operation: 'channel-read', id: 'recovery', ...state.channel });
        check(recoveredPosts.length === 2 && recoveredPosts.filter(value => value.messageId === state.channelMessageId && value.ref.sequence === state.channelSequence && value.body?.text === 'channel accepted before process termination 😀').length === 1
            && sdk.canonicalJson(recoveredPosts.map(value => ({ messageId: value.messageId, sequence: value.ref.sequence, text: value.body!.text! }))) === sdk.canonicalJson(dotnetPosts.posts), 'Recovered channel differs from the independently accepted .NET history');
        pass('verified channel history recovers the accepted post exactly once at its original sequence');
        await command({ operation: 'channel-edit', id: 'recovery', ...state.channel, sequence: state.channelSequence, text: '.NET edit after native publication recovery' });
        await channels.loadChannelHistory(state.channel);
        check((await all(channels.getPosts({ channelId: state.channel.channelId }))).find(value => value.messageId === state.channelMessageId)?.body?.text === '.NET edit after native publication recovery', 'Recovered native channel did not apply the .NET moderator edit');
        pass('the recovered native channel applies an independently signed .NET moderator edit');
        const freshPost = await channels.publishPost(state.channel, { body: { contentType: 'text/plain', text: 'native publication after recovery' } });
        const freshHistory = await command<{ posts: { messageId: string; text: string }[] }>({ operation: 'channel-read', id: 'recovery', ...state.channel });
        check(freshHistory.posts.length === 3 && freshHistory.posts.filter(value => value.messageId === freshPost.messageId && value.text === 'native publication after recovery').length === 1, '.NET history duplicated or lost the fresh native publication');
        pass('a new native publication after recovery reaches the actual .NET channel history once');
        const snapshot = await observe(); const direct = snapshot.faults.find(value => value.method === 'message.send')!; const rotation = snapshot.faults.find(value => value.method === 'group.secret.rotation.commit')!;
        const publication = snapshot.faults.find(value => value.method === 'channel.post')!;
        check(snapshot.requests.filter(value => value.actor === 'typescript' && value.method === 'message.send' && value.bodySha256 === direct.bodySha256).length === 2
            && snapshot.requests.filter(value => value.actor === 'typescript' && value.method === 'group.secret.rotation.commit').length === 1
            && snapshot.requests.some(value => value.bodySha256 === rotation.bodySha256)
            && snapshot.requests.filter(value => value.actor === 'typescript' && value.method === 'channel.post' && value.bodySha256 === publication.bodySha256).length === 1,
            'Direct retry changed its ciphertext or accepted rotation/publication was resubmitted'); pass('server hashes prove one identical encrypted retry and no repeated rotation or accepted channel publication');
        for (const member of state.members) { const method = member.kind === 'application' ? 'group.application.submit' : 'group.member.recovery.submit'; const fault = snapshot.faults.find(value => value.method === method)!;
            check(snapshot.requests.filter(value => value.actor === 'typescript' && value.method === method && value.bodySha256 === fault.bodySha256).length === 1, `Approved ${member.kind} submission was repeated`); }
        const errors = [...local.errors, ...members.flatMap(value => value.local.errors), ...snapshot.errors];
        check(errors.length === 0, `Unexpected recovery errors: ${errors.join('; ')}`);
        for (const member of members) await member.local.dispose();
        await local.dispose(); await command({ operation: 'finish' }); const final = await observe(); check(final.closed && final.errors.length === 0 && final.databases.length === 1 && final.databases[0]!.tables.Groups === 3 && final.databases[0]!.tables.Messages! > 0
            && final.databases[0]!.tables.Channels === 1 && final.databases[0]!.tables.ChannelPosts === 3, 'Actual .NET database did not retain exchanged messages/posts');
        await onEvidence(final); pass('recovery completes without background errors and retains the closed actual .NET SQLite state');
    } finally { for (const member of members) await member.local.dispose(); await local.dispose(); await command({ operation: 'finish' }); }
}
