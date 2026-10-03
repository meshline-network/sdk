import { mkdtemp } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, expect, test } from 'vitest';
import { NodeSqliteStore } from '@meshline/storage-node';
import { channelPostCodec, channelEventCodec, type ChannelPost } from '@meshline/sdk';
import { ChannelRepository, type ChannelOperation } from '../../packages/sdk/dist/channels/repository.js';
import { channelFixture } from '../support/channel-fixture.js';
import { removeTestDirectory } from '../support/temp.js';
const resources: NodeSqliteStore[] = []; const directories: string[] = [];
afterEach(async () => { for (const store of resources.splice(0)) await store.dispose(); for (const path of directories.splice(0)) await removeTestDirectory(path); });
async function fixture() {
    const f = channelFixture(); const directory = await mkdtemp(join(tmpdir(), 'meshline-channel-store-')); directories.push(directory);
    const store = new NodeSqliteStore(join(directory, 'state.sqlite')); resources.push(store); await store.migrate(); await store.initialize({ context: f.context.toString(), accountId: f.owner.certificate.account });
    const repository = new ChannelRepository(store, f.context, f.owner.certificate.account);
    return { f, store, repository, post: () => repository.post({ channel: f.channel, sequence: 1 }) };
}
test('forward synchronization commits descriptors, posts, pending acknowledgement and cursor at one revision', async () => {
    const { f, store, repository, post } = await fixture(); const operation: ChannelOperation = { channel: f.channel, method: 'channel.post', payload: f.post.payload };
    await repository.saveOperation(operation); const result = await repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true);
    expect(result.changes.posts[0]!.kind).toBe('added'); expect(result.changes.channel!.descriptor).toEqual(f.descriptor); expect((await post())!.body!.text).toBe('original'); expect(await repository.hasOperation(operation)).toBe(false);
    const snapshot = await store.read([{ collection: 'channels' }, { collection: 'channel_posts' }, { collection: 'channel_descriptors' }, { collection: 'channel_events' }]);
    expect(new Set(snapshot.sets.flat().map(row => row.revision)).size).toBe(1); expect((await repository.get(f.channel)).syncSequence).toBe(1);
});
test('latest-page edits fetch the original and earlier edits before applying omitted fields', async () => {
    const { f, repository, post } = await fixture(); const first = f.edit(2, { body: { contentType: 'text/plain', text: 'changed body' } }); const second = f.edit(3, { additionalProperties: { retained: 'latest' } });
    f.timeline = [f.initial, f.post, first, second]; const result = await repository.readPage(f.channel, { channelId: f.channel.channelId, limit: 1 }, f.reader, false);
    expect(result.page.events.map(value => value.sequence)).toEqual([3]); expect(f.reads).toEqual([{ channelId: f.channel.channelId, limit: 1 }, { channelId: f.channel.channelId, after: 0 }]);
    expect((await post())!.body!.text).toBe('changed body'); expect((await repository.get(f.channel)).syncSequence).toBe(-1);
    await repository.readPage(f.channel, { channelId: f.channel.channelId, before: 2 }, f.reader, false); expect((await post())!.body!.text).toBe('changed body');
});
test('a verified deletion survives backward history even when the original was pruned', async () => {
    const { f, repository, post } = await fixture(); f.timeline = [f.deletion(4)]; await repository.readPage(f.channel, { channelId: f.channel.channelId }, f.reader, false); expect(await post()).toBeUndefined();
    f.timeline = [f.initial, f.post]; await repository.readPage(f.channel, { channelId: f.channel.channelId }, f.reader, false); expect(await post()).toBeUndefined();
    expect(await repository.publication(f.channel, f.post.payload.value as ChannelPost)).toBe(1);
});
test('catch-up observes a later deletion without resurrecting the post from an older edit page', async () => {
    const { f, repository, post } = await fixture(); const edited = f.edit(2, { body: { contentType: 'text/plain', text: 'old edit' } }); const deletion = f.deletion(3); f.timeline = [f.initial, f.post, edited, deletion];
    const pending: ChannelOperation = { channel: f.channel, method: 'channel.post.delete', payload: deletion.payload }; await repository.saveOperation(pending);
    await repository.readPage(f.channel, { channelId: f.channel.channelId, before: 3, limit: 1 }, f.reader, false); expect(await post()).toBeUndefined(); expect(await repository.hasOperation(pending)).toBe(false);
});
test('catch-up cannot confirm another post publication before its projection is committed', async () => {
    const { f, store, repository, post } = await fixture();
    const other = f.event(2, { kind: 'post', value: { ...(f.post.payload.value as ChannelPost), messageId: 'msg_EBESEwQFBgcICQoLDA0ODw', body: { contentType: 'text/plain', text: 'pending other post' } } });
    const pending: ChannelOperation = { channel: f.channel, method: 'channel.post', payload: other.payload };
    await repository.saveOperation(pending);
    f.timeline = [f.initial, f.post, other, f.edit(3, { body: { contentType: 'text/plain', text: 'requested post edit' } })];
    await repository.readPage(f.channel, { channelId: f.channel.channelId, limit: 1 }, f.reader, false);
    expect((await post())!.body!.text).toBe('requested post edit');
    expect(await repository.post({ channel: f.channel, sequence: 2 })).toBeUndefined();
    expect(await repository.hasOperation(pending)).toBe(true);
    await store.dispose(); const reopened = new NodeSqliteStore(store.path); resources.push(reopened); await reopened.initialize({ context: f.context.toString(), accountId: f.owner.certificate.account });
    const resumed = new ChannelRepository(reopened, f.context, f.owner.certificate.account); expect(await resumed.hasOperation(pending)).toBe(true);
    const applied = await resumed.readPage(f.channel, { channelId: f.channel.channelId, after: 1, limit: 1 }, f.reader, false);
    expect(await resumed.hasOperation(pending)).toBe(false);
    expect(applied.changes.posts).toHaveLength(1); expect(applied.changes.posts[0]!.info!.body!.text).toBe('pending other post');
});
test('catch-up cannot confirm a future edit outside the requested projection boundary', async () => {
    const { f, repository, post } = await fixture(); const later = f.edit(4, { body: { contentType: 'text/plain', text: 'pending later edit' } });
    const pending: ChannelOperation = { channel: f.channel, method: 'channel.post.edit', payload: later.payload }; await repository.saveOperation(pending);
    f.timeline = [f.initial, f.post, f.edit(2, { body: { contentType: 'text/plain', text: 'earlier' } }), f.edit(3, { additionalProperties: { retained: 'requested' } }), later];
    await repository.readPage(f.channel, { channelId: f.channel.channelId, before: 4, limit: 1 }, f.reader, false);
    expect((await post())!.body!.text).toBe('earlier'); expect(await repository.hasOperation(pending)).toBe(true);
    await repository.readPage(f.channel, { channelId: f.channel.channelId, after: 3 }, f.reader, false);
    expect((await post())!.body!.text).toBe('pending later edit'); expect(await repository.hasOperation(pending)).toBe(false);
});
test('missing original leaves the full page and forward cursor uncommitted', async () => {
    const { f, store, repository } = await fixture(); f.timeline = [f.edit(3, { body: { contentType: 'text/plain', text: 'no original' } })];
    await expect(repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true)).rejects.toThrow('Original');
    expect((await store.read([{ collection: 'channels' }, { collection: 'channel_posts' }, { collection: 'channel_events' }, { collection: 'channel_descriptors' }])).sets).toEqual([[], [], [], []]);
});
test('a bad signature in the final event rolls back otherwise valid earlier page entries', async () => {
    const { f, repository } = await fixture(); const corrupted = channelEventCodec.decode(channelEventCodec.encode(f.edit(2, { body: { contentType: 'text/plain', text: 'bad' } }))); corrupted.payload.value.deviceSignature[0]! ^= 1;
    f.timeline = [f.initial, f.post, corrupted]; await expect(repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true)).rejects.toThrow('signature');
    expect((await repository.get(f.channel)).syncSequence).toBe(-1); expect(await repository.post({ channel: f.channel, sequence: 1 })).toBeUndefined();
});
test('verified sequence equivocation and pending publication conflicts never replace saved content', async () => {
    const { f, repository, post } = await fixture(); await repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true);
    const changed = f.event(1, { kind: 'post', value: { ...(f.post.payload.value as ChannelPost), body: { contentType: 'text/plain', text: 'equivocation' } } });
    f.timeline = [f.initial, changed]; await expect(repository.readPage(f.channel, { channelId: f.channel.channelId }, f.reader, false)).rejects.toThrow('conflicts'); expect((await post())!.body!.text).toBe('original');
    const request = channelPostCodec.decode(channelPostCodec.encode(changed.payload.value as ChannelPost)); await expect(repository.publication(f.channel, request)).rejects.toThrow('differs');
});
test('history preparation cannot overwrite concurrent follow changes; unrelated store writes can commit', async () => {
    const { f, store, repository } = await fixture(); await repository.saveDescriptor(f.channel, f.descriptors.get(0)!);
    let once = true; f.beforeRead = async () => { if (!once) return; once = false; await repository.follow(f.channel, true); };
    await expect(repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true)).rejects.toThrow('changed'); expect((await repository.get(f.channel)).isFollowed).toBe(true); expect((await repository.get(f.channel)).syncSequence).toBe(-1);
    f.beforeRead = async () => { await store.commit((await store.read([])).version, [{ kind: 'put', collection: 'unrelated', key: '1', value: { text: 'outside transaction' } }]); };
    await repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true); expect((await repository.get(f.channel)).syncSequence).toBe(1);
});
test('post permission is checked at its accepted descriptor revision, even after moderator removal and closure', async () => {
    const { f, repository, post } = await fixture(); const closed = f.sign({ kind: 'descriptor', value: { ...f.descriptor, moderators: [], revision: 1, status: 'closed' as const, updatedAt: 1730000005 } });
    f.descriptors.set(1, { descriptor: closed.value, signerCertificate: f.owner.certificate }); await repository.saveDescriptor(f.channel, f.descriptors.get(1)!);
    f.timeline = [f.initial, f.event(1, f.post.payload, 0, f.moderator), f.event(5, closed, 1)];
    await repository.readPage(f.channel, { channelId: f.channel.channelId, after: -1 }, f.reader, true); expect((await post())!.author).toBe(f.moderator.certificate.account); expect((await repository.get(f.channel)).descriptor!.status).toBe('closed');
    const invalid = f.event(6, f.post.payload, 1, f.moderator); f.timeline = [invalid]; await expect(repository.readPage(f.channel, { channelId: f.channel.channelId, after: 5 }, f.reader, true)).rejects.toThrow('authorized');
});
