import { afterEach, expect, test, vi } from 'vitest';
import { accountDeviceStateCodec, accountGroupHistorySecretSyncCodec, accountGroupPrivateStateSyncCodec, certificateId, createIdentifier, deriveResourceId, encodeBase64Url, encryptionPublicKey, systemRandom,
    StateConflictError, type JsonObject, type MessageManager, type QueryReader, type SecretProtector } from '@meshline/sdk';
import { GroupAccountSync } from '../../packages/sdk/dist/groups/account-sync.js';
import { GroupSecrets } from '../../packages/sdk/dist/groups/secrets.js';
import { sendGroupAccountPayload } from '../../packages/sdk/dist/messages/account-sender.js';
import type { AccountMessage } from '../../packages/sdk/dist/messages/repository.js';
import { GroupNetwork } from '../support/group-network.js';
import { context } from '../support/relay-fixture.js';

const networks: GroupNetwork[] = []; afterEach(async () => { vi.restoreAllMocks(); for (const value of networks.splice(0)) await value.dispose(); });
async function all<T>(reader: Promise<QueryReader<T>>) { const value = await reader; try { return await value.readNext(100); } finally { await value.dispose(); } }
async function setup() { const network = new GroupNetwork(); networks.push(network); const client = await network.client(80); return { network, client }; }
function state(network: GroupNetwork, key = new Uint8Array(32).fill(131)) { const relayId = network.network.descriptor.relayId; return { groupId: deriveResourceId('group', network.network.states.keys().next().value!, relayId, systemRandom.bytes(16), context), relayId, memberEncryptionPrivateKey: key }; }
function harness(network: GroupNetwork, client: Awaited<ReturnType<GroupNetwork['client']>>, payloads: readonly JsonObject[], protector: SecretProtector = client.protector) {
    const entries: AccountMessage[] = payloads.map((payload, index) => ({ payload, localSequence: index + 1, sender: client.accountId, recipient: client.accountId, senderDeviceId: certificateId(client.device.certificate, context), messageId: createIdentifier('message') }));
    const messages = { readTimeline: async (after: number, count: number) => entries.filter(value => value.localSequence > after).slice(0, count) } as unknown as MessageManager;
    const secrets = new GroupSecrets({ store: client.store, context, accountId: client.accountId, deviceId: () => certificateId(client.device.certificate, context), protector }); const rejected: { id: string; code: string }[] = [];
    const consumer = new GroupAccountSync({ store: client.store, context, accountId: client.accountId, clock: network.network.clock, device: client.device, messages, secrets, rejected: (id, error) => rejected.push({ id, code: error.code }) });
    return { entries, secrets, rejected, consume: () => consumer.consume(new AbortController().signal) };
}

test('a new authorized device restores the current member key and old epoch messages through encrypted self-account messages', async () => {
    const { network, client: owner } = await setup(); const group = (await owner.groups.createGroup(network.network.descriptor.relayId, { name: 'Device recovery', memberCapacity: 10 })).ref;
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'old epoch' } }); await owner.groups.rotateSecret(group, { rotateOwnerMemberKey: true });
    await owner.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'current epoch' } });
    const second = await network.client(80); await owner.messages.start(); await owner.groups.start(); await second.messages.start(); await second.groups.start();
    await network.network.until(async () => (await all(second.groups.getMessages())).length === 2);
    expect((await all(second.groups.getMessages())).map(value => value.body!.text)).toEqual(['old epoch', 'current epoch']); expect((await all(second.groups.getGroups()))[0]!.membership).toBe('member');
    const memberRows = (await second.store.read([{ collection: 'group_member_keys' }])).sets[0]!; expect(memberRows).toHaveLength(1);
    const current = network.groups.get(group.groupId)!.state.members[0]!.memberEncryptionPublicKey; expect(memberRows[0]!.value.publicKey).toBe(encodeBase64Url(current));
    const sent = await second.groups.sendMessage(group, { body: { contentType: 'text/plain', text: 'restored device sends' } }); await owner.groups.getGroup(group); expect((await all(owner.groups.getMessages())).at(-1)!.messageId).toBe(sent.messageId);
    const records = (await second.store.read([{ collection: 'messages' }])).sets[0]!.filter(row => ['meshline.account.group.state.sync', 'meshline.account.group.history_secret.sync'].includes(String(row.value.payloadType)));
    expect(records.length).toBeGreaterThan(1); expect(records.every(row => row.value.payload === undefined && typeof row.value.protectedPayload === 'string')).toBe(true);
    await second.dispose(); const resumed = await network.client(80, second.path); expect((await all(resumed.groups.getMessages())).map(value => value.body!.text)).toContain('old epoch');
});

test('a member-key broadcast is queued atomically with its shared flag and does not enter direct message history', async () => {
    const { network, client } = await setup(); const observed: string[][] = [];
    const commit = client.store.commit.bind(client.store); vi.spyOn(client.store, 'commit').mockImplementation(async (version, mutations, signal) => { observed.push(mutations.map(value => value.collection)); return commit(version, mutations, signal); });
    const group = (await client.groups.createGroup(network.network.descriptor.relayId, { name: 'Atomic sharing', memberCapacity: 5 })).ref;
    expect(observed.some(names => names.includes('group_member_keys') && names.includes('message_outbox'))).toBe(true);
    const queued = (await client.store.read([{ collection: 'message_outbox' }])).sets[0]!.length; await client.groups.getGroup(group);
    expect((await client.store.read([{ collection: 'message_outbox' }])).sets[0]!.length).toBe(queued); expect(await all(client.messages.getMessageHistory())).toEqual([]);
});

test('local protection failure leaves an entire multi-key message and its consumer cursor uncommitted for restart', async () => {
    const { network, client } = await setup(); const states = [state(network), state(network, new Uint8Array(32).fill(132))]; let locked = true; let calls = 0; const inputs: Uint8Array[] = [];
    const protector: SecretProtector = { ...client.protector, async protect(bytes, purpose, signal) { inputs.push(bytes); if (locked && ++calls === 2) throw new Error('keychain unavailable'); return client.protector.protect(bytes, purpose, signal); } };
    const consumer = harness(network, client, [accountGroupPrivateStateSyncCodec.encode({ states })], protector);
    await expect(consumer.consume()).rejects.toThrow('protection failed'); expect(consumer.rejected).toEqual([]);
    expect((await client.store.read([{ collection: 'group_member_keys' }, { collection: 'group_account_meta' }, { collection: 'groups' }])).sets).toEqual([[], [], []]); expect(inputs.every(bytes => bytes.every(value => value === 0))).toBe(true);
    locked = false; const resumed = harness(network, client, consumer.entries.map(value => value.payload), protector); await resumed.consume();
    const rows = (await client.store.read([{ collection: 'group_member_keys' }, { collection: 'group_account_meta' }])).sets; expect(rows[0]).toHaveLength(2); expect(rows[1]![0]!.value.sequence).toBe(1);
});

test('failed atomic persistence neither advances the cursor nor publishes a partial batch', async () => {
    const { network, client } = await setup(); const consumer = harness(network, client, [accountGroupPrivateStateSyncCodec.encode({ states: [state(network), state(network)] })]);
    const commit = client.store.commit.bind(client.store); let fail = true;
    vi.spyOn(client.store, 'commit').mockImplementation(async (version, mutations, signal) => { if (fail && mutations.some(value => value.collection === 'group_account_meta')) throw new Error('disk unavailable'); return commit(version, mutations, signal); });
    await expect(consumer.consume()).rejects.toThrow('disk unavailable'); expect((await client.store.read([{ collection: 'group_member_keys' }, { collection: 'group_account_meta' }])).sets).toEqual([[], []]);
    fail = false; await consumer.consume(); expect((await client.store.read([{ collection: 'group_member_keys' }])).sets[0]).toHaveLength(2);
});

test('a relay rebinding attempt rejects the whole batch, records the rejection, then consumes later valid messages', async () => {
    const { network, client } = await setup(); const original = state(network); const valid = state(network);
    const initial = harness(network, client, [accountGroupPrivateStateSyncCodec.encode({ states: [original] })]); await initial.consume();
    const consumer = harness(network, client, [initial.entries[0]!.payload, accountGroupPrivateStateSyncCodec.encode({ states: [valid, { ...original, relayId: '0x' + '8'.repeat(40) }] }), accountGroupPrivateStateSyncCodec.encode({ states: [valid] })]);
    await consumer.consume(); expect(consumer.rejected.map(value => value.code)).toEqual(['invalid_binding']);
    const rows = (await client.store.read([{ collection: 'group_member_keys' }, { collection: 'group_account_meta' }, { collection: 'group_account_rejections' }])).sets;
    expect(rows[0]).toHaveLength(2); expect(rows[1]![0]!.value.sequence).toBe(3); expect(rows[2]).toHaveLength(1);
});

test('conflicting history secrets reject the entire batch without replacing existing epochs or creating authority', async () => {
    const { network, client } = await setup(); const group = state(network); const original = new Uint8Array(32).fill(201);
    const consumer = harness(network, client, [accountGroupHistorySecretSyncCodec.encode({ secrets: [{ groupId: group.groupId, epoch: 3, applicationSecret: original }] }),
        accountGroupHistorySecretSyncCodec.encode({ secrets: [{ groupId: group.groupId, epoch: 4, applicationSecret: original }, { groupId: group.groupId, epoch: 3, applicationSecret: new Uint8Array(32).fill(202) }] })]);
    await consumer.consume(); expect(consumer.rejected.map(value => value.code)).toEqual(['conflicting_epoch_secret']);
    const rows = (await client.store.read([{ collection: 'groups' }, { collection: 'group_epochs' }, { collection: 'group_application_secrets' }])).sets;
    expect(rows[0]).toEqual([]); expect(rows[1]).toEqual([]); expect(rows[2]).toHaveLength(1); expect(rows[2]![0]!.value.epoch).toBe(3); expect(await consumer.secrets.readApplicationSecret(group, 3)).toBeUndefined();
});

test('authorization is rechecked inside the key-and-cursor transaction after a concurrent device revocation', async () => {
    const { network, client } = await setup(); const second = await network.client(80); const candidate = state(network); const consumer = harness(network, client, [accountGroupPrivateStateSyncCodec.encode({ states: [candidate] })]);
    const commit = client.store.commit.bind(client.store); let revoke = true;
    vi.spyOn(client.store, 'commit').mockImplementation(async (version, mutations, signal) => {
        if (revoke && mutations.some(value => value.collection === 'group_account_meta')) {
            revoke = false; await second.device.publishDeviceState(network.network.descriptor.relayId, { certificates: [second.device.certificate] });
            const snapshot = await client.store.read([]); await commit(snapshot.version, [{ kind: 'put', collection: 'device_states', key: client.accountId, value: accountDeviceStateCodec.encode(network.network.states.get(client.accountId)!) }]);
            throw new StateConflictError('Concurrent authorization update');
        }
        return commit(version, mutations, signal);
    });
    await consumer.consume(); expect(consumer.rejected).toHaveLength(1); expect((await client.store.read([{ collection: 'group_member_keys' }])).sets[0]).toEqual([]);
});

test('a historically delivered sync message from a now revoked device cannot restore private state', async () => {
    const { network, client } = await setup(); const second = await network.client(80); const candidate = state(network);
    await sendGroupAccountPayload(client.messages, accountGroupPrivateStateSyncCodec.encode({ states: [candidate] }), [certificateId(second.device.certificate, context)]);
    await client.messages.start(); await second.messages.start(); await network.network.until(async () => (await second.messages.readTimeline(0, 100)).some(value => value.payload['$type'] === 'meshline.account.group.state.sync')); await second.messages.stop(); await client.messages.stop();
    const received = (await second.messages.readTimeline(0, 100)).find(value => value.payload['$type'] === 'meshline.account.group.state.sync')!;
    await second.device.publishDeviceState(network.network.descriptor.relayId, { certificates: [second.device.certificate] }); await second.groups.start();
    await network.network.until(async () => (await second.store.read([{ collection: 'group_account_rejections' }])).sets[0]!.length === 1);
    expect((await second.store.read([{ collection: 'group_member_keys' }])).sets[0]).toEqual([]);
    expect((await second.store.read([{ collection: 'group_account_meta' }])).sets[0]![0]!.value.sequence).toBeGreaterThanOrEqual(received.localSequence);
});

test('late historical member keys coexist without choosing current group membership', async () => {
    const { network, client } = await setup(); const group = state(network); const oldKey = new Uint8Array(32).fill(135);
    const consumer = harness(network, client, [accountGroupPrivateStateSyncCodec.encode({ states: [group] }), accountGroupPrivateStateSyncCodec.encode({ states: [{ ...group, memberEncryptionPrivateKey: oldKey }] })]);
    await consumer.consume(); const rows = (await client.store.read([{ collection: 'group_member_keys' }, { collection: 'groups' }])).sets;
    expect(new Set(rows[0]!.map(row => row.value.publicKey))).toEqual(new Set([encodeBase64Url(encryptionPublicKey(group.memberEncryptionPrivateKey)), encodeBase64Url(encryptionPublicKey(oldKey))]));
    expect(rows[1]![0]!.value.projection).toBeUndefined(); expect(await all(client.groups.getGroups())).toEqual([]);
});
