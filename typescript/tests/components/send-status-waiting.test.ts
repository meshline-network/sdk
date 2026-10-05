import { afterEach, expect, test, vi } from 'vitest';
import { createIdentifier, type MessageSendState, type MessageSendStatus } from '@meshline/sdk';
import { decodeOutbox, encodeOutbox, outboxKey } from '../../packages/sdk/dist/messages/outbox.js';
import { MessagingNetwork } from '../support/messaging-network.js';

const networks: MessagingNetwork[] = [];
afterEach(async () => { vi.restoreAllMocks(); for (const network of networks.splice(0)) await network.dispose(); });
async function fixture() {
    const network = new MessagingNetwork(); networks.push(network);
    const a = await network.client(21);
    const queued = await a.messages.sendMessage(a.accountId, { body: { contentType: 'text/plain', text: 'wait for delivery' } });
    return { network, a, queued };
}

test.each<[MessageSendState, MessageSendState]>([
    ['queued', 'queued'], ['submissionUnknown', 'queued'], ['relayAccepted', 'relayAccepted'],
    ['targetAccepted', 'relayAccepted'], ['targetAccepted', 'targetAccepted'], ['failed', 'relayAccepted'],
    ['failed', 'targetAccepted'], ['canceled', 'targetAccepted'],
])('retained %s satisfies %s with the actual local status', async (state, target) => {
    const { network, a, queued } = await fixture();
    const snapshot = await a.store.read([outboxKey(queued.messageId)]);
    const record = decodeOutbox(snapshot.sets[0]![0]!);
    await a.store.commit(snapshot.version, [{ kind: 'put', ...outboxKey(queued.messageId), value: encodeOutbox({ ...record, state,
        ...(['relayAccepted', 'targetAccepted'].includes(state) ? { acceptedAt: network.clock.wall } : {}),
        ...(state === 'failed' ? { errorMessage: 'delivery rejected' } : {}) }) }]);
    const status = await a.messages.waitForSendStatus(queued.messageId, target);
    expect(status?.state).toBe(state);
    if (state === 'failed') expect(status?.errorMessage).toBe('delivery rejected');
    expect(network.submissions).toHaveLength(0);
});

test.each([false, true])('waits for acceptance milestones across stop/start (delivery failure: %s)', async failDelivery => {
    const { network, a, queued } = await fixture(); network.deliveryStatus = 'delivering';
    let completed: MessageSendStatus | undefined;
    const target = a.messages.waitForSendStatus(queued.messageId).then(value => { completed = value; return value; });
    const relay = a.messages.waitForSendStatus(queued.messageId, 'relayAccepted');
    await a.messages.start();
    expect((await relay)?.state).toBe('relayAccepted');
    await a.messages.stop(); expect(completed).toBeUndefined();
    if (failDelivery) network.handleRequest = async (method, body, account) => method === 'message.delivery.status'
        ? new Response(JSON.stringify({ status: 'failed', accepted_at: network.deliveries.get(`${account}|${body.message_id}`)!.acceptedAt,
            error: { code: 'message_expired', message: 'delivery expired' } }), { headers: { 'content-type': 'application/json' } }) : undefined;
    network.deliveryStatus = 'target_accepted'; network.clock.tick();
    await a.messages.start();
    expect(await target).toMatchObject({ state: failDelivery ? 'failed' : 'targetAccepted', acceptedRelayId: network.descriptor.relayId });
    if (failDelivery) expect(completed?.errorMessage).toBe('delivery expired');
});

test('canceling a wait leaves both the send and another waiter active', async () => {
    const { a, queued } = await fixture(); const controller = new AbortController();
    const canceled = a.messages.waitForSendStatus(queued.messageId, undefined, controller.signal);
    const rejected = expect(canceled).rejects.toMatchObject({ name: 'AbortError' });
    const other = a.messages.waitForSendStatus(queued.messageId);
    // Let the operations register before canceling; this exercises listener cleanup.
    await a.messages.getSendStatus(queued.messageId);
    controller.abort(); await rejected;
    expect((await a.messages.getSendStatus(queued.messageId))?.state).toBe('queued');
    await a.messages.start();
    expect((await other)?.state).toBe('targetAccepted');
});

test('a commit during the initial read is not lost even when the read returns the old snapshot', async () => {
    const { a, queued } = await fixture(); const read = a.store.read.bind(a.store); let intercept = true;
    vi.spyOn(a.store, 'read').mockImplementation(async (queries, signal) => {
        const snapshot = await read(queries, signal);
        if (intercept && queries.length === 1 && queries[0]?.key === queued.messageId) {
            intercept = false;
            await a.messages.cancelMessage(queued.messageId);
        }
        return snapshot;
    });
    expect((await a.messages.waitForSendStatus(queued.messageId))?.state).toBe('canceled');
});

test('failed commits do not finish waits; committed terminal eviction does', async () => {
    const { a, queued } = await fixture();
    const snapshot = await a.store.read([outboxKey(queued.messageId)]);
    const original = decodeOutbox(snapshot.sets[0]![0]!);
    await a.store.commit(snapshot.version, Array.from({ length: 1000 }, () => {
        const id = createIdentifier('message');
        return { kind: 'put' as const, ...outboxKey(id), value: encodeOutbox({ ...original, state: 'canceled',
            request: { ...original.request, envelope: { ...original.request.envelope, messageId: id, createdAt: queued.createdAt + 1 } } }) };
    }));
    let completed = false;
    const waiting = a.messages.waitForSendStatus(queued.messageId).then(value => { completed = true; return value; });
    const commit = vi.spyOn(a.store, 'commit').mockRejectedValueOnce(new Error('commit failed'));
    await expect(a.messages.cancelMessage(queued.messageId)).rejects.toThrow('commit failed');
    expect(completed).toBe(false); commit.mockRestore();
    a.messages.on('sendStatusChanged', () => { throw new Error('observer failed'); });
    expect(await a.messages.cancelMessage(queued.messageId)).toBe(true);
    expect((await waiting)?.state).toBe('canceled');
    expect(await a.messages.getSendStatus(queued.messageId)).toBeUndefined();
    expect(await a.messages.waitForSendStatus(queued.messageId)).toBeUndefined();
});

test('disposal cancels and drains active waits without canceling the persisted send', async () => {
    const { a, queued } = await fixture();
    const waiting = a.messages.waitForSendStatus(queued.messageId);
    const rejected = expect(waiting).rejects.toMatchObject({ name: 'AbortError' });
    await a.messages.getSendStatus(queued.messageId);
    await a.messages.dispose(); await rejected;
    expect(decodeOutbox((await a.store.read([outboxKey(queued.messageId)])).sets[0]![0]!).state).toBe('queued');
});

test('unknown records return undefined; invalid targets and already aborted signals fail promptly', async () => {
    const { a, queued } = await fixture();
    expect(await a.messages.waitForSendStatus(createIdentifier('message'))).toBeUndefined();
    for (const target of ['submitting', 'submissionUnknown', 'failed', 'canceled', 'invalid'])
        await expect(a.messages.waitForSendStatus(queued.messageId, target as MessageSendState)).rejects.toThrow(RangeError);
    expect(() => a.messages.waitForSendStatus(queued.messageId, undefined, AbortSignal.abort())).toThrow();
});
