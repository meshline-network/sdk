import { readFileSync } from 'node:fs';
import { test, expect, type Page } from '@playwright/test';
import type {} from './harness.js';

async function ready(page: Page): Promise<void> {
    await page.goto('/tests/browser/');
    await page.waitForFunction(() => Boolean(window.meshlineHarness));
}

test.beforeEach(async ({ page }) => { await ready(page); });

test('real IndexedDB commits records with cursors and reopens persistent state', async ({ page }) => {
    const result = await page.evaluate(async () => {
        const harness = window.meshlineHarness;
        await harness.open('restart');
        await harness.stores.main!.commit(0, [
            { kind: 'put', collection: 'messages', key: '001', value: { text: 'persisted 中文😀' } },
            { kind: 'put', collection: 'cursors', key: 'relay', value: { after: 1 } },
        ]);
        await harness.stores.main!.dispose();
        await harness.open('restart', 'reopened', false);
        const result = await harness.stores.reopened!.read([{ collection: 'messages' }, { collection: 'cursors' }]);
        await harness.stores.reopened!.dispose();
        return result;
    });
    expect(result.version).toBe(1);
    expect(result.sets[0]![0]!.value).toEqual({ text: 'persisted 中文😀' });
    expect(result.sets[1]![0]!.value).toEqual({ after: 1 });
});

test('another tab cannot overwrite a stale version or clean a live snapshot', async ({ page, context }) => {
    const other = await context.newPage();
    await ready(other);
    await page.evaluate(async () => {
        const h = window.meshlineHarness;
        await h.open('cross-tab');
        await h.stores.main!.commit(0, [
            { kind: 'put', collection: 'messages', key: '001', value: { text: 'old' } },
            { kind: 'put', collection: 'messages', key: '002', value: { text: 'deleted later' } },
        ]);
        h.readers.main = await h.stores.main!.openQuery({ collection: 'messages' });
    });
    await other.evaluate(async () => {
        const h = window.meshlineHarness;
        await h.open('cross-tab');
        await h.stores.main!.commit(1, [
            { kind: 'put', collection: 'messages', key: '001', value: { text: 'new' } },
            { kind: 'delete', collection: 'messages', key: '002' },
            { kind: 'put', collection: 'messages', key: '003', value: { text: 'inserted' } },
        ]);
    });
    const result = await page.evaluate(async () => {
        const h = window.meshlineHarness;
        let conflicted = false;
        try { await h.stores.main!.commit(1, [{ kind: 'put', collection: 'cursors', key: 'relay', value: { after: 9 } }]); }
        catch (error) { if (!(error instanceof h.sdk.StateConflictError)) throw error; conflicted = true; }
        const first = await h.readers.main!.readNext(1);
        const rest = await h.readers.main!.readNext(10);
        const done = await h.readers.main!.readNext(1);
        const current = await h.stores.main!.read([{ collection: 'messages' }, { collection: 'cursors' }]);
        await h.readers.main!.dispose();
        return { conflicted, first, rest, done, current, snapshots: await h.count('cross-tab', 'snapshots') };
    });
    expect(result.conflicted).toBe(true);
    expect(result.first[0]!.value).toEqual({ text: 'old' });
    expect(result.rest[0]!.value).toEqual({ text: 'deleted later' });
    expect(result.done).toEqual([]);
    expect(result.current.sets[0]!.map(row => row.value.text)).toEqual(['new', 'inserted']);
    expect(result.current.sets[1]).toEqual([]);
    expect(result.snapshots).toBe(0);
});

test('closing a tab releases ownership so abandoned snapshots can be recovered', async ({ page, context }) => {
    await page.evaluate(async () => {
        const h = window.meshlineHarness;
        await h.open('crash');
        await h.stores.main!.commit(0, [{ kind: 'put', collection: 'messages', key: '001', value: { text: 'keep' } }]);
        h.readers.main = await h.stores.main!.openQuery({ collection: 'messages' });
    });
    const other = await context.newPage();
    await ready(other);
    expect(await other.evaluate(() => window.meshlineHarness.count('crash', 'snapshots'))).toBe(1);
    await page.close();
    // Page closure and the browser process releasing Web Locks are separate events.
    await expect.poll(() => other.evaluate(async () => (await navigator.locks.query()).held?.filter(lock => lock.name?.startsWith('meshline:crash:')).length ?? 0)).toBe(0);
    const result = await other.evaluate(async () => {
        const h = window.meshlineHarness;
        await h.open('crash');
        return { snapshots: await h.count('crash', 'snapshots'), state: await h.stores.main!.read([{ collection: 'messages' }]) };
    });
    expect(result.snapshots).toBe(0);
    expect(result.state.sets[0]![0]!.value).toEqual({ text: 'keep' });
});

test('a failure after the first write rolls back both data and revision', async ({ page }) => {
    const result = await page.evaluate(async () => {
        const h = window.meshlineHarness;
        await h.open('rollback');
        const original = IDBObjectStore.prototype.put;
        IDBObjectStore.prototype.put = function(value: unknown, key?: IDBValidKey): IDBRequest<IDBValidKey> {
            if (this.name === 'records' && (value as { key: string }).key === '002') throw new Error('injected write failure');
            return key === undefined ? original.call(this, value) : original.call(this, value, key);
        };
        let failure = '';
        try {
            await h.stores.main!.commit(0, [
                { kind: 'put', collection: 'messages', key: '001', value: { text: 'must roll back' } },
                { kind: 'put', collection: 'messages', key: '002', value: { text: 'fails' } },
            ]);
        } catch (error) { failure = (error as Error).message; }
        finally { IDBObjectStore.prototype.put = original; }
        return { failure, state: await h.stores.main!.read([{ collection: 'messages' }]) };
    });
    expect(result.failure).toBe('injected write failure');
    expect(result.state).toEqual({ version: 0, sets: [[]] });
});

test('prefix queries are literal and concurrent reader calls advance without duplicates', async ({ page }) => {
    const result = await page.evaluate(async () => {
        const h = window.meshlineHarness;
        await h.open('queries');
        await h.stores.main!.commit(0, ['a%001', 'a%002', 'abc'].map((key, index) => ({ kind: 'put' as const, collection: 'messages', key, value: { index } })));
        const reader = await h.stores.main!.openQuery({ collection: 'messages', prefix: 'a%', reverse: true });
        const [first, second] = await Promise.all([reader.readNext(1), reader.readNext(1)]);
        const done = await reader.readNext(1);
        await reader.dispose();
        return { keys: [...first, ...second].map(row => row.key), done };
    });
    expect(result).toEqual({ keys: ['a%002', 'a%001'], done: [] });
});

test('browser crypto validates the same independent certificate and rejects another network', async ({ page }) => {
    const result = await page.evaluate(async vector => {
        const sdk = window.meshlineHarness.sdk;
        const row = vector.device_certificate;
        const certificate = sdk.deviceCertificateCodec.decode({ ...row.unsigned_object, device_signature: row.device_signature, account_signature: row.account_signature });
        const network = sdk.NetworkContext.parse(vector.network_context);
        sdk.validateCertificate(certificate, network);
        let rejected = false;
        try { sdk.validateCertificate(certificate, new sdk.NetworkContext(network.reference + 1, network.registry)); }
        catch (error) { if (!(error instanceof sdk.ProtocolError)) throw error; rejected = true; }
        return { id: sdk.certificateId(certificate, network), expected: vector.device_identity.derived_device_id, rejected };
    }, JSON.parse(readFileSync(new URL('../../../tests/vectors/identity-auth-v1.json', import.meta.url), 'utf8')));
    expect(result.id).toBe(result.expected);
    expect(result.rejected).toBe(true);
});
