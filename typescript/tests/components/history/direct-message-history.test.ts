import { afterEach, expect, test, vi } from 'vitest';
import type { QueryReader } from '@meshline/sdk';
import { ClientNetwork } from '../../support/client-network.js';
import { createConversationFixture } from '../../support/conversation-fixture.js';

const resources: { dispose(): Promise<void> }[] = [];
afterEach(async () => {
    vi.restoreAllMocks();
    for (const resource of resources.splice(0).reverse()) await resource.dispose();
});
async function all<T>(query: Promise<QueryReader<T>>) {
    const reader = await query;
    try { return await reader.readNext(100); } finally { await reader.dispose(); }
}
function fixture() {
    const network = new ClientNetwork();
    resources.push(network);
    return createConversationFixture(network);
}

test.each([false, true])('direct history batches follow local sequence despite older creation times (filter peer: %s)', async filterPeer => {
    const f = await fixture(); await f.incoming('first'); await f.incoming('second', f.network.clock.wall - 20);
    const reader = await f.client.messageManager.getMessageHistory(filterPeer ? f.peer.certificate.account : undefined);
    try {
        expect((await reader.readNext(1))[0]!.body!.text).toBe('first');
        await f.incoming('third', f.network.clock.wall - 40);
        expect((await reader.readNext(1))[0]!.body!.text).toBe('second'); expect(await reader.readNext(1)).toEqual([]);
    } finally { await reader.dispose(); }
    expect((await all(f.client.messageManager.getMessageHistory(filterPeer ? f.peer.certificate.account : undefined))).map(value => value.body!.text)).toEqual(['first', 'second', 'third']);
});
