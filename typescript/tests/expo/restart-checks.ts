import * as sdk from '@meshline/sdk';
import { ExpoSqliteStore, expoRandom } from '@meshline/expo';
import { File, Paths } from 'expo-file-system';
import type { VectorResult } from './portable-vectors';

const context = sdk.NetworkContext.parse('neo:860833102:0x5979ba79431672a38a18a32cdc48fd7317818b70');
// Public acceptance keys, never an application Keychain/Keystore implementation.
const accountKey = new Uint8Array(32).fill(17); const master = new Uint8Array(32).fill(53);
const publicKey = sdk.accountPublicKey(accountKey); const accountId = sdk.getAccountId('neo:860833102', publicKey);
const binding = { context: context.toString(), accountId };
const stateFile = () => new File(Paths.document, 'meshline-restart-state.json');
function check(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
function protector(wrong = false): sdk.SecretProtector {
    return {
        async protect(bytes, purpose) { const nonce = expoRandom.bytes(12); return sdk.concatBytes(nonce, sdk.encryptAes(master, nonce, bytes, sdk.encodeUtf8(purpose))); },
        async unprotect(bytes, purpose) { return sdk.decryptAes(wrong ? new Uint8Array(32).fill(54) : master, bytes.slice(0, 12), bytes.slice(12), sdk.encodeUtf8(purpose)); },
    };
}
async function open(name: string, migrate: boolean, wrong = false) {
    const store = new ExpoSqliteStore({ databaseName: name });
    const registry: sdk.RelayRegistry = { context, async getRelay() { return undefined; }, async *getRelays() {} };
    const pool = new sdk.RelayClientPool({ context, accountId, random: expoRandom, fetch: async () => { throw new Error('Offline restart check attempted network I/O.'); } }, registry);
    const accountSigner: sdk.AccountSigner = { accountId, publicKey, async sign(input) { return sdk.signAccount(input, accountKey, expoRandom); } };
    const account = new sdk.AccountManager({ context, accountId, store, relayClients: pool, accountSigner });
    const device = new sdk.DeviceManager({ context, accountId, store, relayClients: pool, accountManager: account, accountSigner, secretProtector: protector(wrong), random: expoRandom });
    const dispose = async () => { await device.dispose(); await account.dispose(); await pool.dispose(); await store.dispose(); };
    try { if (migrate) await store.migrate(); await account.initialize(); await device.initialize(); return { store, device, dispose }; }
    catch (error) { await dispose(); throw error; }
}

export async function prepareRestart(packageManifestSha256: string): Promise<void> {
    const name = `meshline-restart-${Date.now()}.sqlite`; const runtime = await open(name, true);
    try {
        const certificate = await runtime.device.createDevice(3600); const id = sdk.certificateId(certificate, context);
        const snapshot = await runtime.store.read([{ collection: 'local_device' }, { collection: 'identity_binding' }]);
        const deviceRecord = snapshot.sets[0]?.[0]; const bindingRecord = snapshot.sets[1]?.[0];
        check(deviceRecord && bindingRecord && deviceRecord.revision === bindingRecord.revision, 'Device and binding did not commit atomically.');
        const marker = sdk.encodeBase64Url(expoRandom.bytes(32));
        await runtime.store.commit(snapshot.version, [
            { kind: 'put', collection: 'acceptance', key: 'message', value: { marker, sequence: 7 } },
            { kind: 'put', collection: 'acceptance', key: 'cursor', value: { sequence: 7 } },
        ]);
        stateFile().write(JSON.stringify({ name, id, marker, packageManifestSha256, preparedAt: new Date().toISOString(), certificate: sdk.deviceCertificateCodec.encode(certificate) }));
    } finally { await runtime.dispose(); }
}

export async function verifyRestart(packageManifestSha256: string, onResult: (result: VectorResult) => void): Promise<void> {
    const state = JSON.parse(await stateFile().text()) as { name: string; id: string; marker: string; packageManifestSha256: string; certificate: sdk.JsonValue };
    check(state.packageManifestSha256 === packageManifestSha256, 'Restart state belongs to another package.');
    const runtime = await open(state.name, false);
    async function run(name: string, action: () => Promise<void>) {
        try { await action(); onResult({ name, passed: true }); } catch (error) { onResult({ name, passed: false, error: String(error) }); }
    }
    try {
        await run('protected device identity survives process restart', async () => {
            check(sdk.certificateId(runtime.device.certificate, context) === state.id, 'Device identity changed.');
            check(sdk.deviceCertificateCodec.stringify(runtime.device.certificate) === sdk.canonicalJson(state.certificate), 'Certificate changed.');
            const input = sdk.encodeUtf8('Android protected-key restart 😀');
            check(sdk.verifyDevice(input, await runtime.device.sign(input), runtime.device.certificate.signingPublicKey), 'Recovered signing key failed verification.');
        });
        await run('protected encryption key survives process restart', async () => {
            const peer = expoRandom.bytes(32);
            try { check(sdk.encodeBase64Url(await runtime.device.deriveSharedSecret(sdk.encryptionPublicKey(peer))) === sdk.encodeBase64Url(sdk.agreeKey(peer, runtime.device.certificate.encryptionPublicKey)), 'Recovered agreement key differs.'); }
            finally { peer.fill(0); }
        });
        await run('committed records and cursor survive process restart', async () => {
            const snapshot = await runtime.store.read([{ collection: 'acceptance', key: 'message' }, { collection: 'acceptance', key: 'cursor' }]);
            const message = snapshot.sets[0]?.[0]; const cursor = snapshot.sets[1]?.[0];
            check(message?.value.marker === state.marker && message.value.sequence === 7 && cursor?.value.sequence === 7 && message.revision === cursor.revision, 'Atomic persisted records differ.');
        });
        await run('stale native transaction leaves records and cursor unchanged', async () => {
            const before = await runtime.store.read([{ collection: 'acceptance' }]); let rejected = false;
            try { await runtime.store.commit(before.version - 1, [{ kind: 'put', collection: 'acceptance', key: 'cursor', value: { sequence: 99 } }]); }
            catch (error) { if (!(error instanceof sdk.StateConflictError)) throw error; rejected = true; }
            check(rejected, 'Stale writer was accepted.');
            check(sdk.canonicalJson((await runtime.store.read([{ collection: 'acceptance' }])).sets[0]!.map(row => row.value)) === sdk.canonicalJson(before.sets[0]!.map(row => row.value)), 'Stale writer changed records.');
        });
        await run('restored native query keeps its snapshot across a writer commit', async () => {
            const reader = await runtime.store.openQuery({ collection: 'acceptance', key: 'message' });
            try {
                const current = await runtime.store.read([]); await runtime.store.commit(current.version, [{ kind: 'put', collection: 'acceptance', key: 'message', value: { marker: state.marker, sequence: 8 } }]);
                check((await reader.readNext(1))[0]?.value.sequence === 7, 'Reader snapshot advanced to the later commit.');
            } finally { await reader.dispose(); }
        });
        await run('wrong protection key fails without replacing persisted identity', async () => {
            const before = await runtime.store.read([{ collection: 'local_device' }, { collection: 'identity_binding' }]);
            const wrong = await open(state.name, false, true); let rejected = false;
            try { try { await wrong.device.sign(sdk.encodeUtf8('must fail')); } catch (error) { if (!(error instanceof sdk.ProtocolError) || error.code !== 'invalid_ciphertext') throw error; rejected = true; } }
            finally { await wrong.dispose(); }
            check(rejected, 'Wrong protection key was accepted.');
            check(sdk.canonicalJson((await runtime.store.read([{ collection: 'local_device' }, { collection: 'identity_binding' }])).sets.map(rows => rows.map(row => row.value))) === sdk.canonicalJson(before.sets.map(rows => rows.map(row => row.value))), 'Failed key recovery replaced stored identity.');
        });
    } finally { await runtime.dispose(); }
}
