import { ExpoSqliteStore, expoRandom, expoRelayFetch } from '@meshline/expo';
import { File, Paths } from 'expo-file-system';
import { runInteropChecks } from '../portable/interop.js';
import type { VectorResult } from './portable-vectors';

export async function runNativeInteropChecks(onResult: (result: VectorResult) => void): Promise<void> {
    const diagnostics: string[] = []; const file = new File(Paths.document, 'meshline-native-interop-diagnostics.json'); file.write('[]');
    const waits: unknown[] = []; const waitFile = new File(Paths.document, 'meshline-native-interop-waits.json'); waitFile.write('[]');
    await runInteropChecks({ origin: 'https://127.0.0.1:18445', fetch: expoRelayFetch, random: expoRandom,
        createStore: databaseName => new ExpoSqliteStore({ databaseName }),
        onDiagnostic: message => { diagnostics.push(message); file.write(JSON.stringify(diagnostics, null, 2)); },
        onWait: value => { waits.push(value); waitFile.write(JSON.stringify(waits, null, 2)); } }, onResult,
    evidence => { new File(Paths.document, 'meshline-native-interop-evidence.json').write(JSON.stringify(evidence, null, 2)); });
}
