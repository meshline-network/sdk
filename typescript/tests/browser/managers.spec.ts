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
