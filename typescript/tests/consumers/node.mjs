import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { MeshlineClient, NetworkContext, RelayClientPool, accountPublicKey, concatBytes, decryptAes, encodeUtf8, encryptAes, getAccountId, signAccount, systemRandom, verifyDevice } from '@meshline/sdk';
import { NodeSqliteStore } from '@meshline/storage-node';
import { IndexedDbStore } from '@meshline/storage-browser';
import { createNodeRelayFetch, createNodeSocketFactory } from '@meshline/transport-node';

// Public disposable test identity, confined to the generated consumer directory.
const key = new Uint8Array(32).fill(121); const context = NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
const publicKey = accountPublicKey(key); const accountId = getAccountId('neo:860833102', publicKey);
const registry = { context, async getRelay() { return undefined; }, async *getRelays() {} };
const signer = { accountId, publicKey, sign: async data => signAccount(data, key) };
const protector = { async protect(data, purpose) { const nonce = systemRandom.bytes(12); return concatBytes(nonce, encryptAes(key, nonce, data, encodeUtf8(purpose))); },
    async unprotect(data, purpose) { return decryptAes(key, data.slice(0, 12), data.slice(12), encodeUtf8(purpose)); } };
const path = resolve('smoke.sqlite');
async function open() {
    const store = new NodeSqliteStore(path); await store.migrate();
    const pool = new RelayClientPool({ context, accountId, fetch: createNodeRelayFetch(), socketFactory: createNodeSocketFactory() }, registry);
    const client = new MeshlineClient({ context, accountId, store, relayClients: pool, secretProtector: protector, accountSigner: signer }); await client.initialize();
    return { store, pool, client, async dispose() { await client.dispose(); await pool.dispose(); await store.dispose(); } };
}
assert.equal(typeof IndexedDbStore, 'function');
const first = await open(); let certificate;
try { certificate = await first.client.deviceManager.createDevice(3600); assert.equal(first.client.deviceState, undefined); }
finally { await first.dispose(); }
const second = await open();
try {
    const input = encodeUtf8('installed tarball restart test'); assert.deepEqual(second.client.device.signingPublicKey, certificate.signingPublicKey);
    assert.equal(verifyDevice(input, await second.client.deviceManager.sign(input), certificate.signingPublicKey), true);
    const query = await second.client.getConversations(); try { assert.deepEqual(await query.readNext(10), []); } finally { await query.dispose(); }
    const rows = await second.store.read([{ collection: 'local_device', key: 'current' }]); assert.equal(typeof rows.sets[0][0].value.protectedSigningKey, 'string');
} finally { await second.dispose(); key.fill(0); }
console.log('Installed Node packages: exports, SQLite migration, protected device restart and conversation query passed.');
