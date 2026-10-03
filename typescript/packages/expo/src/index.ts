import { fetch as nativeFetch } from 'expo/fetch';
import { getRandomValues } from 'expo-crypto';
import { File } from 'expo-file-system';
import { defaultDatabaseDirectory, openDatabaseAsync } from 'expo-sqlite';
import { type RandomSource } from '@meshline/sdk';
import { NativeSqliteStore } from './sqlite-store.js';
import { requireNativeModule } from 'expo';
import { nativeSocketFactory, type NativeSocketModule } from './socket.js';
import type { RelaySocketFactory } from '@meshline/sdk';
import { databaseLocation } from './database-path.js';
import { createExpoRelayFetch } from './http.js';

/** Requires a development/production build containing this package's native module. */
export function createExpoSocketFactory(): RelaySocketFactory {
    return nativeSocketFactory(requireNativeModule<NativeSocketModule>('MeshlineRelaySocket'));
}

export interface ExpoSqliteStoreOptions {
    /** Persistent database basename, not an absolute path or SQLite connection URI. */
    readonly databaseName: string;
    /** Absolute app-private directory path or local file URI. Omit to use expo-sqlite's default. */
    readonly directory?: string;
}

/** Native SQLite storage for Expo Modules applications; constructors never open or create files. */
export class ExpoSqliteStore extends NativeSqliteStore {
    constructor(options: ExpoSqliteStoreOptions) {
        const location = databaseLocation(options.directory ?? defaultDatabaseDirectory, options.databaseName);
        super({
            async exists() { return new File(location.fileUri).exists; },
            open() { return openDatabaseAsync(location.encodedName, { useNewConnection: true }, location.directoryUri); },
        });
    }
}

/** This API always uses the native cryptographic source, including development builds. */
export const expoRandom: RandomSource = {
    bytes(length) {
        if (!Number.isSafeInteger(length) || length < 0) throw new RangeError('Invalid random byte count.');
        const bytes = new Uint8Array(length);
        for (let offset = 0; offset < length; offset += 65536) getRandomValues(bytes.subarray(offset, offset + 65536));
        return bytes;
    },
};

/** Uses expo/fetch explicitly: React Native's legacy global fetch cannot enforce these transport policies. */
export const expoRelayFetch = createExpoRelayFetch((url, init) => nativeFetch(url, init));
