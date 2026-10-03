import { ProtocolError } from '../errors.js';
import { validateAccountId } from '../identity/neo.js';
import { NetworkContext } from '../protocol/context.js';
import { canonicalJson, parseJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';

/** A storage instance belongs to one network/account and its persisted local device. */
export interface StorageBinding { readonly context: string; readonly accountId: string }

export interface RecordKey { readonly collection: string; readonly key: string }
export interface StoredRecord extends RecordKey { readonly revision: number; readonly value: JsonObject }

/** Keys are canonical ASCII SDK identifiers or sortable indexes, never user-facing text. */
export interface RecordQuery {
    readonly collection: string;
    readonly key?: string;
    readonly prefix?: string;
    readonly reverse?: boolean;
}

export type StoreMutation = (RecordKey & { readonly kind: 'put'; readonly value: JsonObject })
    | (RecordKey & { readonly kind: 'delete' });

export interface StoreSnapshot { readonly version: number; readonly sets: readonly (readonly StoredRecord[])[] }

/** Fixed at open time. An empty batch marks completion; dispose releases the snapshot. */
export interface QueryReader<T> {
    readNext(count: number, signal?: AbortSignal): Promise<readonly T[]>;
    dispose(): Promise<void>;
}

/**
 * Atomic compare-and-commit storage. Computation, signing, and network I/O happen
 * outside database transactions. A conflicting commit must be recomputed from a
 * new read; it must never blindly replay an external business request.
 */
export interface MeshlineStore {
    migrate(signal?: AbortSignal): Promise<void>;
    initialize(binding: StorageBinding, signal?: AbortSignal): Promise<void>;
    read(queries: readonly RecordQuery[], signal?: AbortSignal): Promise<StoreSnapshot>;
    commit(expectedVersion: number, mutations: readonly StoreMutation[], signal?: AbortSignal): Promise<number>;
    openQuery(query: RecordQuery, signal?: AbortSignal): Promise<QueryReader<StoredRecord>>;
    dispose(): Promise<void>;
}

export function bindingJson(binding: StorageBinding): string {
    NetworkContext.parse(binding.context);
    validateAccountId(binding.accountId);
    return canonicalJson({ context: binding.context, accountId: binding.accountId });
}

export function validateRecordKey(value: RecordKey): void {
    if (typeof value.collection !== 'string' || !/^[a-z][a-z0-9_.-]{0,63}$/.test(value.collection) || /[^a-z0-9_.-]/.test(value.collection))
        throw new ProtocolError('invalid_storage_key', 'Collection must be a lowercase SDK identifier.');
    if (typeof value.key !== 'string' || value.key.length > 4096 || /[^\x20-\x7e]/.test(value.key))
        throw new ProtocolError('invalid_storage_key', 'Record keys must be at most 4096 printable ASCII characters.');
}

export function validateRecordQuery(query: RecordQuery): void {
    if (query.key !== undefined && query.prefix !== undefined)
        throw new ProtocolError('invalid_storage_query', 'Specify either key or prefix.');
    validateRecordKey({ collection: query.collection, key: query.key ?? query.prefix ?? '' });
    if (query.reverse !== undefined && typeof query.reverse !== 'boolean') throw new ProtocolError('invalid_storage_query', 'reverse must be boolean.');
}

/** Copies and validates a commit before an adapter opens its write transaction. */
export function prepareCommit(expectedVersion: number, mutations: readonly StoreMutation[]): readonly StoreMutation[] {
    requireSafeInteger(expectedVersion, 0);
    if (expectedVersion === Number.MAX_SAFE_INTEGER) throw new ProtocolError('revision_exhausted', 'Storage revision is exhausted.');
    if (mutations.length === 0) throw new ProtocolError('empty_commit', 'A commit requires at least one mutation.');
    const keys = new Set<string>();
    return mutations.map(mutation => {
        validateRecordKey(mutation);
        const id = mutation.collection + '\0' + mutation.key;
        if (keys.has(id)) throw new ProtocolError('duplicate_mutation', 'A record may be changed only once per commit.');
        keys.add(id);
        if (mutation.kind === 'put') return { ...mutation, value: requireObject(parseJson(canonicalJson(mutation.value))) };
        if (mutation.kind === 'delete') return { ...mutation };
        throw new ProtocolError('invalid_mutation', 'Unknown store mutation.');
    });
}

export function requireBatchCount(count: number): void { requireSafeInteger(count, 1); }

/** Preserves fixed snapshot semantics while projecting stored rows into domain results. */
export function mapQueryReader<T, U>(reader: QueryReader<T>, project: (value: T) => U): QueryReader<U> {
    return {
        async readNext(count, signal) { return (await reader.readNext(count, signal)).map(project); },
        dispose() { return reader.dispose(); },
    };
}
