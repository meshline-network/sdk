import { File, Paths } from 'expo-file-system';
import { ExpoSqliteStore, expoRandom, expoRelayFetch, createExpoSocketFactory } from '@meshline/expo';
import { runReconnectChecks } from '../portable/reconnect.js';
import type { VectorResult } from './portable-vectors';

export async function runNativeReconnectChecks(onResult: (result: VectorResult) => void): Promise<void> {
    const databaseName = `meshline-reconnect-${Date.now()}.sqlite`;
    await runReconnectChecks({ origin: 'https://127.0.0.1:18443', fetch: expoRelayFetch, socketFactory: createExpoSocketFactory(), random: expoRandom,
        createStore: () => new ExpoSqliteStore({ databaseName }) }, onResult,
    evidence => new File(Paths.document, 'meshline-native-reconnect-evidence.json').write(JSON.stringify(evidence, null, 2)));
}
