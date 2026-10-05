import { afterEach, expect, test, vi } from 'vitest';
import { MessageManager, StateConflictError, contactRecordCodec, contactGrantCodec, certificateId, messageSendRequestCodec, type QueryReader, type MessageEvents } from '@meshline/sdk';
import { context } from '../support/relay-fixture.js';
import { MessagingNetwork } from '../support/messaging-network.js';

const networks: MessagingNetwork[] = [];
afterEach(async () => { for (const network of networks.splice(0)) await network.dispose(); });
async function fixture() { const network = new MessagingNetwork(); networks.push(network); const a = await network.client(21); const b = await network.client(22); return { network, a, b }; }
async function all<T>(reader: Promise<QueryReader<T>>): Promise<readonly T[]> { const query = await reader; try { return await query.readNext(100); } finally { await query.dispose(); } }
async function connect(f: Awaited<ReturnType<typeof fixture>>) {
    const { network, a, b } = f; await a.messages.start(); await b.messages.start();
    await a.messages.addContact(b.accountId, '你好');
    await network.until(async () => (await all(b.messages.getContactRequests({ direction: 'incoming' }))).length === 1);
    await b.messages.acceptContactRequest(a.accountId);
    await network.until(async () => Boolean(await a.messages.getContact(b.accountId)));
}

test('MessageManager establishes contacts, exchanges encrypted direct messages and queries stable history', async () => {
    const f = await fixture(); await connect(f); const { network, a, b } = f;
    expect(await a.messages.getContact(b.accountId)).toMatchObject({ state: 'active', grantFromContact: 'valid', grantToContact: 'valid' });
    expect(await b.messages.getContact(a.accountId)).toMatchObject({ state: 'active', grantFromContact: 'valid', grantToContact: 'valid' });
    const sent = await a.messages.sendMessage(b.accountId, { body: { contentType: 'text/plain', text: '完整 SDK 工作流 😀' } }); expect(sent.state).toBe('queued');
    await network.until(async () => Boolean(await b.messages.getMessage({ sender: a.accountId, messageId: sent.messageId })));
    expect((await b.messages.getMessage({ sender: a.accountId, messageId: sent.messageId }))!.body!.text).toBe('完整 SDK 工作流 😀');
    await network.until(async () => (await a.messages.getSendStatus(sent.messageId))?.state === 'targetAccepted');
    expect(await all(a.messages.getMessageHistory(b.accountId))).toHaveLength(1); expect(await all(a.messages.getMessageHistory(a.accountId))).toHaveLength(0);
    expect(await all(a.messages.getContactRequests())).toEqual([]); expect(await all(b.messages.getContactRequests())).toEqual([]);
    expect(a.messages.lastBackgroundError).toBeUndefined(); expect(b.messages.lastBackgroundError).toBeUndefined();
});

test('ungranted direct sends are rejected; self messages persist locally and queued cancellation prevents transmission', async () => {
    const { a, b, network } = await fixture();
    await expect(a.messages.sendMessage(b.accountId, { body: { contentType: 'text/plain', text: 'not authorized' } })).rejects.toThrow('grant');
    const sent = await a.messages.sendMessage(a.accountId, { body: { contentType: 'text/plain', text: 'self message' } });
    expect(await a.messages.cancelMessage(sent.messageId)).toBe(true); expect(await a.messages.cancelMessage(sent.messageId)).toBe(false);
    await a.messages.start(); await network.until(async () => network.submissions.length > 0);
    expect(network.submissions.some(request => request.envelope.messageId === sent.messageId)).toBe(false);
    expect(await all(a.messages.getMessageHistory(a.accountId))).toHaveLength(1); expect((await a.messages.getSendStatus(sent.messageId))!.state).toBe('canceled');
});

test('contact invitation, duplicate outgoing request, dismissal and queued request cancellation retain clear local state', async () => {
    const { a, b } = await fixture(); const invitation = await b.messages.createInvite(1730003000);
    const first = await a.messages.addContact(invitation, '通过邀请添加'); const duplicate = await a.messages.addContact(invitation, 'second note'); expect(duplicate.messageId).toBe(first.messageId);
    expect((await all(a.messages.getContactRequests({ direction: 'outgoing' })))[0]).toMatchObject({ note: '通过邀请添加', sendState: 'queued' });
    expect(await a.messages.cancelMessage(first.messageId)).toBe(true); expect(await all(a.messages.getContactRequests())).toEqual([]);
    await a.messages.dismissContactRequest(b.accountId); await expect(a.messages.acceptContactRequest(b.accountId)).rejects.toThrow('no incoming');
});

test('contact request events distinguish additions and removals without phantom contact changes', async () => {
    const { a, b } = await fixture(); const requests: MessageEvents['contactRequestChanged'][] = []; const contacts: MessageEvents['contactChanged'][] = [];
    a.messages.on('contactRequestChanged', value => { requests.push(value); }); a.messages.on('contactChanged', value => { contacts.push(value); });
    const request = await a.messages.addContact(b.accountId, 'one request'); await a.messages.addContact(b.accountId);
    expect(requests).toEqual([{ accountId: b.accountId, direction: 'outgoing', kind: 'added', request }]);
    await a.messages.cancelMessage(request.messageId); await a.messages.cancelMessage(request.messageId); await a.messages.dismissContactRequest(b.accountId);
    expect(requests).toEqual([{ accountId: b.accountId, direction: 'outgoing', kind: 'added', request }, { accountId: b.accountId, direction: 'outgoing', kind: 'removed' }]);
    expect(contacts).toEqual([]);
});

test('request delivery events retain each committed send state and dismiss only existing incoming requests', async () => {
    const { a, b, network } = await fixture(); const outgoing: MessageEvents['contactRequestChanged'][] = []; const incoming: MessageEvents['contactRequestChanged'][] = [];
    a.messages.on('contactRequestChanged', value => { outgoing.push(value); }); b.messages.on('contactRequestChanged', value => { incoming.push(value); });
    const request = await a.messages.addContact(b.accountId, 'delivery snapshots'); await a.messages.start();
    await network.until(async () => (await a.messages.getSendStatus(request.messageId))?.state === 'targetAccepted');
    expect(outgoing.map(value => value.request?.sendState)).toEqual(['queued', 'submitting', 'targetAccepted']);
    expect(outgoing).toMatchObject([{ kind: 'added' }, { kind: 'updated' }, { kind: 'updated' }]);
    await b.messages.start(); await network.until(async () => incoming.length > 0); await b.messages.stop();
    await b.messages.dismissContactRequest(a.accountId); await b.messages.dismissContactRequest(a.accountId);
    expect(incoming).toEqual([{ accountId: a.accountId, direction: 'incoming', kind: 'added', request: expect.objectContaining({ messageId: request.messageId, note: 'delivery snapshots' }) },
        { accountId: a.accountId, direction: 'incoming', kind: 'removed' }]);
});

test('contact events expose relationship, alias and deletion snapshots from successful transactions', async () => {
    const f = await fixture(); const { a, b } = f; const changes: MessageEvents['contactChanged'][] = [];
    a.messages.on('contactChanged', value => { changes.push(value); }); await connect(f); await a.messages.stop(); await b.messages.stop();
    expect(changes).toContainEqual({ accountId: b.accountId, kinds: ['relationship', 'authorization'], contact: expect.objectContaining({ accountId: b.accountId, state: 'active', grantFromContact: 'valid', grantToContact: 'valid' }) });
    changes.length = 0; await a.messages.setContactAlias(b.accountId, 'first'); await a.messages.setContactAlias(b.accountId, 'second'); await a.messages.removeContact(b.accountId);
    expect(changes).toEqual([{ accountId: b.accountId, kinds: ['alias'], contact: expect.objectContaining({ alias: 'first' }) },
        { accountId: b.accountId, kinds: ['alias'], contact: expect.objectContaining({ alias: 'second' }) }, { accountId: b.accountId, kinds: ['deleted'] }]);
});

test('failed and retried contact transactions never publish uncommitted or duplicate events', async () => {
    const f = await fixture(); await connect(f); const { a, b } = f; await a.messages.stop(); await b.messages.stop();
    const changes: MessageEvents['contactChanged'][] = []; a.messages.on('contactChanged', value => { changes.push(value); });
    const commit = a.store.commit.bind(a.store); let failures = 1; let conflict = false;
    const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (...args) => {
        if (args[1].some(value => value.collection === 'contacts')) {
            expect(changes).toEqual([]);
            if (failures-- > 0) throw conflict ? new StateConflictError('contended store') : new Error('disk full');
        }
        return commit(...args);
    });
    try {
        await expect(a.messages.setContactAlias(b.accountId, 'failed')).rejects.toThrow('disk full'); expect(changes).toEqual([]);
        expect((await a.messages.getContact(b.accountId))!.alias).toBeUndefined();
        conflict = true; failures = 1; await a.messages.setContactAlias(b.accountId, 'committed');
        expect(changes).toEqual([{ accountId: b.accountId, kinds: ['alias'], contact: expect.objectContaining({ alias: 'committed' }) }]);
    } finally { spy.mockRestore(); }
});

test('alias/removal persist and enqueue encrypted account synchronization atomically', async () => {
    const f = await fixture(); await connect(f); const { a, b, network } = f; await a.messages.stop();
    await a.messages.setContactAlias(b.accountId, '测试联系人'); expect((await a.messages.getContact(b.accountId))!.alias).toBe('测试联系人');
    expect(await all(a.messages.getContacts('测试'))).toHaveLength(1);
    const before = (await a.store.read([{ collection: 'message_outbox' }])).sets[0]!.length;
    await a.messages.removeContact(b.accountId); expect(await a.messages.getContact(b.accountId)).toBeUndefined();
    const rows = await a.store.read([{ collection: 'contacts', key: b.accountId }, { collection: 'message_outbox' }]);
    expect(contactRecordCodec.decode(rows.sets[0]![0]!.value.record!).status).toBe('deleted'); expect(rows.sets[1]!.length).toBe(before + 1);
    await expect(a.messages.sendMessage(b.accountId, { body: { contentType: 'text/plain', text: 'removed contact' } })).rejects.toThrow('grant');
    expect(network.submissions.length).toBeGreaterThan(0);
});

test('alias updates return contact snapshots including empty/cleared values and survive observer disposal', async () => {
    const f = await fixture(); await connect(f); const { a, b } = f; await a.messages.stop(); await b.messages.stop();
    const first = await a.messages.setContactAlias(b.accountId, '私有别名 😀');
    expect(first).toEqual(await a.messages.getContact(b.accountId));
    expect(first).toMatchObject({ accountId: b.accountId, alias: '私有别名 😀', state: 'active', grantFromContact: 'valid', grantToContact: 'valid' });
    const empty = await a.messages.setContactAlias(b.accountId, '');
    expect(empty).toMatchObject({ alias: '' }); expect(first).toMatchObject({ alias: '私有别名 😀' });
    const cleared = await a.messages.setContactAlias(b.accountId, null);
    expect(cleared).toEqual(await a.messages.getContact(b.accountId)); expect(cleared).not.toHaveProperty('alias');
    let disposal: Promise<void> | undefined;
    a.messages.on('contactChanged', () => { disposal = a.messages.dispose(); return disposal; });
    await expect(a.messages.setContactAlias(b.accountId, 'final')).resolves.toMatchObject({ accountId: b.accountId, alias: 'final', state: 'active' });
    expect(disposal).toBeDefined(); await disposal; expect(a.messages.lifecycleState).toBe('disposed');
});

test('an unchanged contact alias returns its snapshot without writes, signing or duplicate sync', async () => {
    const f = await fixture(); await connect(f); const { a, b } = f; await a.messages.stop(); await b.messages.stop();
    for (const alias of ['same', '', null]) {
        await a.messages.setContactAlias(b.accountId, alias);
        const expected = await a.messages.getContact(b.accountId); const before = await a.store.read([]);
        const changes: MessageEvents['contactChanged'][] = []; const detach = a.messages.on('contactChanged', value => { changes.push(value); });
        const sign = vi.spyOn(a.device, 'sign');
        try {
            const actual = await a.messages.setContactAlias(b.accountId, alias);
            expect((await a.store.read([])).version).toBe(before.version);
            expect(sign).not.toHaveBeenCalled(); expect(changes).toEqual([]); expect(actual).toEqual(expected);
        } finally { sign.mockRestore(); detach(); }
    }
});

test('uncertain encrypted sends recover after MessageManager/database restart without creating a new request', async () => {
    const { a, network } = await fixture(); network.loseResponse = true; await a.messages.start();
    const sent = await a.messages.sendMessage(a.accountId, { body: { contentType: 'text/plain', text: 'recover me' } });
    await network.until(async () => (await a.messages.getSendStatus(sent.messageId))?.state === 'submissionUnknown'); await a.dispose();
    network.loseResponse = false; const resumed = await network.client(21, a.path); await resumed.messages.start();
    await network.until(async () => (await resumed.messages.getSendStatus(sent.messageId))?.state === 'targetAccepted');
    const attempts = network.submissions.filter(value => value.envelope.messageId === sent.messageId); expect(attempts.length).toBeGreaterThanOrEqual(2);
    expect(attempts.at(-1)).toEqual(attempts[0]); expect((await resumed.messages.getMessage({ sender: a.accountId, messageId: sent.messageId }))!.body!.text).toBe('recover me');
});

test('runtime message observers can stop MessageManager without deadlocking shared relay cleanup', async () => {
    const f = await fixture(); await connect(f); const { network, a, b } = f; let stopped = false;
    b.messages.on('messageReceived', async () => { await b.messages.stop(); stopped = true; });
    await a.messages.sendMessage(b.accountId, { body: { contentType: 'text/plain', text: 'stop after delivery' } });
    await network.until(async () => stopped); expect(b.messages.lifecycleState).toBe('stopped');
    expect(a.messages).toBeInstanceOf(MessageManager);
});

test('pending application route observers do not delay internal timeline recording and disposal drains its write', async () => {
    const { a, network } = await fixture(); await a.messages.dispose();
    let releaseObserver!: () => void; const observer = new Promise<void>(resolve => { releaseObserver = resolve; });
    const detach = a.account.on('accountChanged', () => observer);
    const messages = new MessageManager({ context, accountId: a.accountId, store: a.store, relayClients: a.pool, accountManager: a.account, deviceManager: a.device, clock: network.clock, secretProtector: a.protector });
    network.resources.push(messages); await messages.initialize();
    const timeline = { collection: 'message_timelines', key: network.descriptor.relayId };
    await a.store.commit((await a.store.read([])).version, [{ ...timeline, kind: 'delete' }]);
    let releaseWrite!: () => void; const writing = new Promise<void>(resolve => { releaseWrite = resolve; }); let entered = false;
    const commit = a.store.commit.bind(a.store); const spy = vi.spyOn(a.store, 'commit').mockImplementation(async (...args) => {
        if (args[1].some(value => value.collection === timeline.collection)) { entered = true; await writing; }
        return commit(...args);
    });
    let disposed = false; let disposal: Promise<void> | undefined;
    const publication = a.account.publishRoute(network.descriptor.relayId, { validitySeconds: 3600 });
    try {
        await vi.waitFor(() => expect(entered).toBe(true)); await publication;
        disposal = messages.dispose().then(() => { disposed = true; });
        await Promise.resolve(); expect(disposed).toBe(false);
        releaseWrite(); await disposal;
        expect((await a.store.read([timeline])).sets[0]).toHaveLength(1);
    } finally { releaseObserver(); releaseWrite(); detach(); spy.mockRestore(); await publication; await disposal; }
});

test('new devices obtain contact snapshots, supplement grants, and safely replace a previous device', async () => {
    const f = await fixture(); await connect(f); const { network, a, b } = f; const oldId = certificateId(a.device.certificate, context);
    await expect(a.device.removeDevice(oldId)).rejects.toThrow('invalidate a contact grant');
    const second = await network.client(21); const secondId = certificateId(second.device.certificate, context); await second.messages.start();
    await network.until(async () => Boolean(await second.messages.getContact(b.accountId)));
    await network.until(async () => {
        const contact = (await second.store.read([{ collection: 'contacts', key: b.accountId }])).sets[0]![0]?.value;
        if (!contact?.confirmedGrantTo) return false; return Boolean(contactGrantCodec.decode(contact.confirmedGrantTo).signatures[secondId]);
    });
    await a.messages.stop(); await second.device.removeDevice(oldId);
    await a.device.getOwnDeviceState(network.descriptor.relayId); expect(a.device.getAuthorizationState(oldId)).toBe('notRegistered');
    expect(network.states.get(a.accountId)!.certificates.map(value => certificateId(value, context))).toEqual([secondId]);
    const sent = await second.messages.sendMessage(b.accountId, { body: { contentType: 'text/plain', text: 'after device replacement' } });
    await network.until(async () => Boolean(await b.messages.getMessage({ sender: a.accountId, messageId: sent.messageId })));
    expect((await second.messages.getContact(b.accountId))!.grantToContact).toBe('valid');
});

test('removal cannot erase the last contact signature merely because another device exists', async () => {
    const f = await fixture(); await connect(f); const { network, a } = f; await a.messages.stop();
    await network.client(21); const id = certificateId(a.device.certificate, context); const before = network.states.get(a.accountId)!.revision;
    await expect(a.device.removeDevice(id)).rejects.toThrow('replacement signatures'); expect(network.states.get(a.accountId)!.revision).toBe(before);
});

test('a consent reply retains source-relay acceptance evidence before final delivery status arrives', async () => {
    const f = await fixture(); f.network.deliveryStatus = 'delivering'; await connect(f);
    const contact = (await f.a.store.read([{ collection: 'contacts', key: f.b.accountId }])).sets[0]![0]!.value;
    expect(contact.confirmedGrantTo).toEqual(contactGrantCodec.encode(contactRecordCodec.decode(contact.record!).grantToContact!));
    expect(contactGrantCodec.decode(contact.confirmedGrantTo!).signatures[certificateId(f.a.device.certificate, context)]).toBeDefined();
    expect((await f.a.store.read([{ collection: 'message_outbox' }])).sets[0]!.some(row => row.value.state === 'relayAccepted')).toBe(true);
});

test('a protocol message with an unreadable local key box reports failure without starving later sends', async () => {
    const { network, a, b } = await fixture(); const broken = await a.messages.addContact(b.accountId);
    const rows = await a.store.read([{ collection: 'message_outbox', key: broken.messageId }]); const row = rows.sets[0]![0]!;
    const request = messageSendRequestCodec.decode(row.value.request!); request.senderBoxes![0]!.sealedKey[15]! ^= 1;
    await a.store.commit(rows.version, [{ kind: 'put', collection: 'message_outbox', key: row.key, value: { ...row.value, request: messageSendRequestCodec.encode(request) } }]);
    network.clock.tick(); const valid = await a.messages.sendMessage(a.accountId, { body: { contentType: 'text/plain', text: 'still deliver this' } });
    const failures: string[] = []; a.messages.onLifecycle('backgroundError', value => { if (value.resource) failures.push(value.resource); });
    await a.messages.start(); await network.until(async () => network.submissions.some(value => value.envelope.messageId === valid.messageId));
    expect(failures).toContain(broken.messageId); expect((await a.messages.getSendStatus(broken.messageId))!.state).toBe('queued');
    expect(network.submissions.some(value => value.envelope.messageId === broken.messageId)).toBe(false);
});
