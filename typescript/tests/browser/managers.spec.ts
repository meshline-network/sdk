import { expect, test } from '@playwright/test';
import type {} from './harness.js';

test('managers, encrypted messages, accepted channel posts and group keys recover in a real browser', async ({ page }) => {
    await page.goto('/tests/browser/'); await page.waitForFunction(() => Boolean(window.meshlineHarness));
    expect(await page.evaluate(() => window.meshlineHarness.managerRoundtrip())).toEqual({
        retriedOriginal: true, recoveredKey: true, nickname: '浏览器 😀', pending: false, protectedKeys: true, recoveredMessage: true, retriedEncryptedMessage: true,
        recoveredChannel: true, channelPublishedOnce: true, recoveredGroup: true, protectedGroupSecrets: true, recoveredGroupAccountSync: true,
        failedGroupSyncUnchanged: true, groupKeyCursorAtomic: true, aliasSnapshot: true, aliasNoop: true,
    });
});

test('account setup resumes an unknown publication in IndexedDB and preserves devices and profile on a new installation', async ({ page }) => {
    await page.goto('/tests/browser/'); await page.waitForFunction(() => Boolean(window.meshlineHarness));
    const result = await page.evaluate(async () => {
        const { sdk, createClientNetwork } = window.meshlineHarness; const network = createClientNetwork();
        try {
            const first = await network.open(); network.loseAfter = 'account.route.publish';
            let reason: string | undefined;
            try { await first.client.establishAccount({ initialProfile: { nickname: '保留资料', publicDiscovery: true } }); }
            catch (error) { if (!(error instanceof sdk.AccountEstablishmentError)) throw error; reason = error.reason; }
            const certificate = first.client.device!; await first.dispose(); network.loseAfter = undefined;
            const resumed = await network.open(91, first.path); await resumed.client.establishAccount();
            const writes = network.requests.filter(value => value.method.endsWith('.publish')).length; const signs = network.signatures.length;
            await resumed.client.establishAccount();
            const healthyReused = writes === network.requests.filter(value => value.method.endsWith('.publish')).length && signs === network.signatures.length;
            const next = await network.open(); await next.client.establishAccount({ initialProfile: { nickname: '忽略默认值' } });
            return { reason, keysReused: sdk.certificateId(resumed.client.device!, network.registry.context) === sdk.certificateId(certificate, network.registry.context),
                healthyReused, devices: next.client.deviceState!.certificates.length, nickname: next.client.profile!.nickname,
                publicDiscovery: next.client.profile!.publicDiscovery,
                pending: (await resumed.store.read([{ collection: 'client_account_operations', key: 'establishment' }])).sets[0]!.length };
        } finally { await network.dispose(); }
    });
    expect(result).toEqual({ reason: 'submissionOutcomeUnknown', keysReused: true, healthyReused: true, devices: 2, nickname: '保留资料', publicDiscovery: true, pending: 0 });
});
