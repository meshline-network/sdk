import { ExpoSqliteStore, expoRandom } from '@meshline/expo';
import { managerRoundtrip } from '../portable/managers.js';
import type { VectorResult } from './portable-vectors';

/** Offline relay fixture with real installed SDK engines and native SQLite. */
export async function runWorkflowChecks(onResult: (result: VectorResult) => void): Promise<void> {
    const databaseName = `meshline-workflows-${Date.now()}.sqlite`;
    const result = await managerRoundtrip({ createStore: () => new ExpoSqliteStore({ databaseName }), random: expoRandom });
    const checks: readonly [string, boolean][] = [
        ['profile retry preserves the exact signed request', result.retriedOriginal],
        ['device signing key restored after store reopen', result.recoveredKey],
        ['profile recovered from the accepted retry', result.nickname === '浏览器 😀'],
        ['accepted profile no longer pending', result.pending === false],
        ['local device stores protected signing material', result.protectedKeys],
        ['encrypted direct message and local history agree after reopen', result.recoveredMessage],
        ['uncertain outbox retries identical ciphertext and then clears', result.retriedEncryptedMessage],
        ['accepted channel post restored from verified history', result.recoveredChannel],
        ['accepted channel post submitted only once', result.channelPublishedOnce],
        ['staged group epoch decrypts nickname and message after reopen', result.recoveredGroup],
        ['stored group and account records contain no plaintext secrets', result.protectedGroupSecrets],
        ['authorized account message recovers group member key and cursor', result.recoveredGroupAccountSync],
        ['failed group key transaction preserves key and cursor state', result.failedGroupSyncUnchanged],
        ['retried group key and cursor share one committed revision', result.groupKeyCursorAtomic],
        ['alias update returns its contact snapshot and survives store reopen', result.aliasSnapshot],
        ['unchanged alias performs no write or duplicate synchronization', result.aliasNoop],
    ];
    for (const [name, passed] of checks) onResult({ name, passed, ...(!passed ? { error: 'Workflow observation did not match the required result.' } : {}) });
}
