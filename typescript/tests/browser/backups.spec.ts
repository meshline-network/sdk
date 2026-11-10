import { expect, test } from '@playwright/test';
import type {} from './harness.js';

test('password backups stream through IndexedDB clients with selected history and idempotent merging', async ({ page }) => {
    await page.goto('/tests/browser/'); await page.waitForFunction(() => Boolean(window.meshlineHarness));
    const result = await page.evaluate(async () => {
        const { sdk } = window.meshlineHarness; const network = window.meshlineHarness.createClientNetwork();
        try {
            const source = await network.open(); await source.client.establishAccount();
            const record = { type: 'message', data: { sender: source.client.accountId, recipient: source.client.accountId, message_id: sdk.createIdentifier('message'), sender_device_id: sdk.certificateId(source.client.device!, source.client.context), created_at: network.clock.wall,
                payload: sdk.directMessageCodec.encode({ body: { contentType: 'text/plain', text: 'browser backup 中文 🔐' } }) } };
            await source.client.messageManager.importBackup((async function* () { yield record; })());
            const chunks: Uint8Array[] = []; await source.client.exportBackup({ write: async value => { chunks.push(value.slice()); } }, ' browser🔐 ', { directHistory: sdk.BackupSelection.all });
            const file = sdk.concatBytes(...chunks); const target = await network.open();
            const input = async function* () { for (let offset = 0; offset < file.length; offset += 13) yield file.subarray(offset, offset + 13); };
            const first = await target.client.importBackup(input(), ' browser🔐 '); const repeated = await target.client.importBackup(input(), ' browser🔐 ');
            const reader = await target.client.messageManager.getMessageHistory(); try { return { first, repeated, messages: (await reader.readNext(10)).map(value => value.body!.text), chunks: chunks.length }; } finally { await reader.dispose(); }
        } finally { await network.dispose(); }
    });
    expect(result.first.applied).toBe(1); expect(result.repeated.applied).toBe(0); expect(result.messages).toEqual(['browser backup 中文 🔐']); expect(result.chunks).toBeGreaterThan(1);
});
