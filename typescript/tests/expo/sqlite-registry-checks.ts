import { openDatabaseAsync } from 'expo-sqlite';
import { Platform } from 'react-native';

// Native regression for the Expo 57.0.19 shared-object registry resize race.
export async function concurrentNativeStatements(): Promise<void> {
    const database = await openDatabaseAsync('meshline-registry-regression.sqlite', { useNewConnection: true });
    try {
        const prepared = await Promise.allSettled(Array.from({ length: 8192 }, () => database.prepareAsync('SELECT 1')));
        const finalized = await Promise.allSettled(prepared.flatMap(value => value.status === 'fulfilled' ? [value.value.finalizeAsync()] : []));
        const errors = [...prepared, ...finalized].flatMap(value => value.status === 'rejected' ? [String(value.reason)] : []);
        if (errors.length) throw new Error(`Native registry regression: ${errors.join('; ')}`);
    } finally { await database.closeAsync(); }
}

export async function releasedNativeStatement(): Promise<void> {
    const database = await openDatabaseAsync('meshline-registry-release-control.sqlite', { useNewConnection: true });
    try {
        const statement = await database.prepareAsync('SELECT 1');
        await statement.finalizeAsync();
        // Fixture-only access to the pinned Expo wrapper proves real releases still fail.
        (statement as unknown as { nativeStatement: { release(): void } }).nativeStatement.release();
        try { await statement.getColumnNamesAsync(); }
        catch (error) {
            const expected = Platform.OS === 'ios'
                ? 'Unable to find the native shared object associated with given JavaScript object'
                : 'Cannot use shared object that was already released';
            if (String(error).includes(expected)) return;
            throw error;
        }
        throw new Error('Access to a released native statement unexpectedly succeeded.');
    } finally { await database.closeAsync(); }
}
