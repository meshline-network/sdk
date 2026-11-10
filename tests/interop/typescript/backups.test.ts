import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, expect, test } from 'vitest';
import { BackupSelection, MeshlineClient, canonicalJson, concatBytes, contactRecordCodec, deriveResourceId, type GroupRef, type JsonObject, type BackupReport } from '@meshline/sdk';
import { DotnetBridge } from '../../../typescript/tests/support/dotnet.js';
import { GroupNetwork } from '../../../typescript/tests/support/group-network.js';
import { context } from '../../../typescript/tests/support/relay-fixture.js';
import { removeTestDirectory } from '../../../typescript/tests/support/temp.js';
import { ClientNetwork } from '../../../typescript/tests/support/client-network.js';
import { createConversationFixture } from '../../../typescript/tests/support/conversation-fixture.js';
import { vector } from '../../../typescript/tests/support/vectors.js';
import { groupVectors } from '../../../typescript/tests/support/group-fixture.js';

const resources: { dispose(): Promise<void> }[] = []; const directories: string[] = [];
afterEach(async () => { for (const resource of resources.splice(0).reverse()) await resource.dispose(); for (const directory of directories.splice(0)) await removeTestDirectory(directory); });
async function* bytes(value: Uint8Array): AsyncGenerator<Uint8Array> { for (let offset = 0; offset < value.length; offset += 113) yield value.subarray(offset, offset + 113); }
async function collect(source: AsyncIterable<JsonObject>): Promise<JsonObject[]> { const result = []; for await (const record of source) result.push(record); return result; }
const password = ' 跨端🔐 cafe\u0301 ';
test('rejected group message metadata survives TypeScript → C# → TypeScript without becoming visible history', async () => {
    const network = new ClientNetwork(); resources.push(network); const source = await network.open();
    const fixture = vector<{ group_restore: { records: JsonObject[] } }>('backup');
    const event = groupVectors.management_chain.chain.find(value => (value.event.payload as JsonObject).$type === 'meshline.group.message')!.event;
    const payload = event.payload as JsonObject; const actor = groupVectors.management_chain.actors.find(value => value.device_id === event.signer_device_id)!;
    const data: JsonObject = { group_id: groupVectors.management_chain.group_id, sequence: event.sequence!, epoch: event.epoch!, payload_json: canonicalJson(payload), certificate_json: canonicalJson(actor.certificate), message_id: payload.message_id!, sender: actor.certificate.account!, sender_device_id: actor.device_id, created_at: new Date(Number(payload.created_at) * 1000).toISOString(), decrypted_payload_json: null, rejection: 'invalid message content', is_message: false };
    const incoming = [...fixture.group_restore.records.slice(0, -1), { type: 'group_event', data }];
    await source.client.groupManager.importBackup((async function* () { yield* incoming; })());
    const output: Uint8Array[] = []; await source.client.exportBackup({ write: async chunk => { output.push(chunk.slice()); } }, password, { groupHistory: BackupSelection.all });
    const bridge = new DotnetBridge(network.fetch); resources.push(bridge); const directory = await mkdtemp(join(tmpdir(), 'meshline-rejected-backup-')); directories.push(directory); const id = 'rejected';
    await bridge.invoke({ operation: 'open', id, context: context.toString(), privateKey: Buffer.alloc(32, 91).toString('base64'), path: join(directory, 'target.sqlite'), now: network.clock.wall, relays: network.relays.map(value => ({ relayId: value.descriptor.relayId, endpoint: value.descriptor.endpoints[0]! })) });
    await bridge.invoke({ operation: 'backup-import', id, password, data: Buffer.from(concatBytes(...output)).toString('base64') });
    const csharp = (await bridge.invoke<{ records: JsonObject[] }>({ operation: 'backup-records', id })).records;
    const exported = await bridge.invoke<{ data: string }>({ operation: 'backup-export', id, password, groupHistory: true });
    const target = await network.open(); const file = Buffer.from(exported.data, 'base64');
    await target.client.importBackup(bytes(file), password); expect((await target.client.importBackup(bytes(file), password)).applied).toBe(0);
    const typescript = await collect(target.client.groupManager.exportBackup(BackupSelection.all));
    for (const rows of [csharp, typescript]) {
        const restored = rows.find(row => row.type === 'group_event' && (row.data as JsonObject).sequence === data.sequence)!.data as JsonObject;
        expect(restored).toMatchObject({ message_id: data.message_id, sender: data.sender, sender_device_id: data.sender_device_id, rejection: data.rejection, decrypted_payload_json: null, is_message: false });
        expect(Date.parse(String(restored.created_at))).toBe(Date.parse(String(data.created_at)));
    }
    const reader = await target.client.groupManager.getMessages(); try { expect(await reader.readNext(10)).toEqual([]); } finally { await reader.dispose(); }
}, 60_000);

test.each(['typescript', 'dotnet'] as const)('%s backup restores into the other SDK, merges existing records and resumes group messaging with restored keys', async direction => {
    const network = new GroupNetwork(); resources.push(network); const local = await network.client(103); const relayId = network.network.descriptor.relayId;
    const client = new MeshlineClient({ context, accountId: local.accountId, store: local.store, relayClients: local.pool, secretProtector: local.protector, clock: network.network.clock }); resources.push(client); await client.initialize();
    const bridge = new DotnetBridge(network.network.fetch); resources.push(bridge); const directory = await mkdtemp(join(tmpdir(), 'meshline-backup-interop-')); directories.push(directory); const id = 'backup';
    await bridge.invoke({ operation: 'open', id, context: context.toString(), privateKey: Buffer.alloc(32, 103).toString('base64'), path: join(directory, 'target.sqlite'), now: network.network.clock.wall, relays: [{ relayId, endpoint: network.network.descriptor.endpoints[0]! }] });
    await bridge.invoke({ operation: 'authorize', id, relayId }); let group: GroupRef;
    const allTs = async () => collect(client.groupManager.exportBackup(BackupSelection.all));
    const allCs = async () => (await bridge.invoke<{ records: JsonObject[] }>({ operation: 'backup-records', id })).records.filter(value => String(value.type).startsWith('group'));
    async function exportFile(history: boolean): Promise<Uint8Array> {
        if (direction === 'dotnet') return Buffer.from((await bridge.invoke<{ data: string }>({ operation: 'backup-export', id, password, groupHistory: history })).data, 'base64');
        const chunks: Uint8Array[] = []; await client.exportBackup({ write: async chunk => { chunks.push(chunk.slice()); } }, password, { groupHistory: history ? BackupSelection.all : BackupSelection.none }); return concatBytes(...chunks);
    }
    const importFile = (value: Uint8Array): Promise<BackupReport> => direction === 'typescript'
        ? bridge.invoke({ operation: 'backup-import', id, password: password.normalize('NFC'), data: Buffer.from(value).toString('base64') })
        : client.importBackup(bytes(value), password.normalize('NFC'));
    if (direction === 'typescript') {
        group = (await client.groupManager.createGroup(relayId, { name: 'portable group 中文', memberCapacity: 20 })).ref;
        await client.groupManager.sendMessage(group, { body: { contentType: 'text/plain', text: 'before restore 🌿' } }); await client.groupManager.setNickname(group, 'restored nickname');
    } else {
        group = await bridge.invoke<GroupRef>({ operation: 'group-create', id, relayId, name: 'portable group 中文' });
        await bridge.invoke({ operation: 'group-send', id, ...group, text: 'before restore 🌿' }); await bridge.invoke({ operation: 'group-nickname', id, ...group, nickname: 'restored nickname' });
    }
    const selected = await exportFile(true); const baseline = await exportFile(false);
    expect((await importFile(baseline)).conflicts).toBe(0);
    let restored = direction === 'typescript' ? await allCs() : await allTs();
    expect(restored.some(value => value.type === 'group_nickname' && (value.data as JsonObject).nickname === 'restored nickname')).toBe(true);
    expect(restored.some(value => value.type === 'group_event' && (value.data as JsonObject).is_message === true)).toBe(false);
    expect((await importFile(selected)).conflicts).toBe(0); expect((await importFile(selected)).applied).toBe(0);
    restored = direction === 'typescript' ? await allCs() : await allTs(); const original = direction === 'typescript' ? await allTs() : await allCs();
    const normalize = (rows: JsonObject[]) => rows.map(row => {
        const data = { ...row.data as JsonObject };
        for (const field of ['payload_json', 'certificate_json', 'decrypted_payload_json']) if (typeof data[field] === 'string') data[field] = JSON.parse(data[field]);
        if (typeof data.created_at === 'string') data.created_at = new Date(data.created_at).toISOString(); return { type: row.type, data };
    });
    expect(normalize(restored)).toEqual(normalize(original));
    await importFile(baseline); expect((direction === 'typescript' ? await allCs() : await allTs()).filter(value => value.type === 'group_event' && (value.data as JsonObject).is_message === true)).toHaveLength(1);
    if (direction === 'typescript') await bridge.invoke({ operation: 'group-send', id, ...group, text: 'restored C# sends' });
    else await client.groupManager.sendMessage(group, { body: { contentType: 'text/plain', text: 'restored TypeScript sends' } });
    expect(network.groups.get(group.groupId)!.events).toHaveLength(4);
}, 60_000);

test('contacts, channel choices and direct history pass TypeScript → C# → a new TypeScript device without outbox or read positions', async () => {
    const network = new ClientNetwork(); resources.push(network); const source = await createConversationFixture(network); const peer = source.peer.certificate.account;
    await source.incoming('旧消息 "quotes" \\path 中文 😀'); const last = await source.incoming('second'); await source.client.markRead(peer, last.localSequence);
    await source.client.messageManager.sendMessage(source.client.accountId, { body: { contentType: 'text/plain', text: 'pending must stay local' } });
    const contact = { type: 'contact', data: contactRecordCodec.encode({ account: peer, alias: 'portable friend', status: 'active', updatedAt: network.clock.wall }) };
    await source.client.messageManager.importBackup((async function* () { yield contact; })());
    const relayId = network.relays[0]!.descriptor.relayId; const channel = { type: 'channel', data: { channel_id: deriveResourceId('channel', source.client.accountId, relayId, new Uint8Array(16), context), relay_id: relayId, is_followed: true } };
    await source.client.channelManager.importBackup((async function* () { yield channel; })());
    const output: Uint8Array[] = []; await source.client.exportBackup({ write: async chunk => { output.push(chunk.slice()); } }, password, { directHistory: BackupSelection.only([peer]) });
    const bridge = new DotnetBridge(network.fetch); resources.push(bridge); const directory = await mkdtemp(join(tmpdir(), 'meshline-direct-backup-interop-')); directories.push(directory); const id = 'direct-backup';
    await bridge.invoke({ operation: 'open', id, context: context.toString(), privateKey: Buffer.alloc(32, 91).toString('base64'), path: join(directory, 'target.sqlite'), now: network.clock.wall, relays: network.relays.map(value => ({ relayId: value.descriptor.relayId, endpoint: value.descriptor.endpoints[0]! })) });
    const request = { operation: 'backup-import', id, password, data: Buffer.from(concatBytes(...output)).toString('base64') };
    expect((await bridge.invoke<BackupReport>(request)).applied).toBe(4); expect((await bridge.invoke<BackupReport>(request)).applied).toBe(0);
    const retained = await bridge.invoke<{ records: JsonObject[] }>({ operation: 'backup-records', id }); expect(retained.records).toContainEqual(contact); expect(retained.records).toContainEqual(channel);
    expect(retained.records.filter(value => value.type === 'message')).toHaveLength(2);
    const exported = await bridge.invoke<{ data: string }>({ operation: 'backup-export', id, password, directHistory: true }); const target = await network.open();
    expect((await target.client.importBackup(bytes(Buffer.from(exported.data, 'base64')), password)).applied).toBe(4);
    const reader = await target.client.messageManager.getMessageHistory(peer); try { expect((await reader.readNext(10)).map(value => value.body!.text)).toEqual(['旧消息 "quotes" \\path 中文 😀', 'second']); } finally { await reader.dispose(); }
    expect((await target.client.getConversation(peer))!.unreadCount).toBe(2); const outbox = await target.client.messageManager.getOutbox(); try { expect(await outbox.readNext(10)).toEqual([]); } finally { await outbox.dispose(); }
}, 60_000);
