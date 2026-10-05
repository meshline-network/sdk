import { requireSafeInteger } from '../protocol/json.js';
import { ProtocolError } from '../errors.js';
import { throwIfAborted } from '../runtime/clock.js';
import { AsyncGate } from '../runtime/async-gate.js';
import { requireBatchCount, type QueryReader, type RecordQuery, type StoredRecord } from '../storage/store.js';

/** Exclusive local sequence bounds. before moves toward older batches; each batch is returned ascending. Managers copy these fields when called. */
export interface HistoryRange {
    /** Nonnegative safe-integer lower bound, excluded from results. */
    readonly after?: number;
    /** Nonnegative safe-integer upper bound, excluded from results; selects the closest earlier batch. */
    readonly before?: number;
}
/** Resolve the original signal argument or the new range/signal overload without relying on a realm-specific AbortSignal constructor. */
export function historyArguments(rangeOrSignal?: HistoryRange | AbortSignal | null, signal?: AbortSignal): { range: HistoryRange; signal: AbortSignal | undefined } {
    if (rangeOrSignal != null && 'aborted' in rangeOrSignal) return { range: {}, signal: rangeOrSignal };
    const range = { ...rangeOrSignal }; historySelection(range);
    return { range, signal };
}
export function historySelection(query: HistoryRange = {}) {
    const { after, before } = query;
    if (after !== undefined) requireSafeInteger(after, 0);
    if (before !== undefined) requireSafeInteger(before, 0);
    if (after !== undefined && before !== undefined && after >= before) throw new RangeError('after must be less than before.');
    return { after, before, reverse: before !== undefined };
}
export const historyIndex = (sequence: number): string => String(sequence).padStart(16, '0');
export function historyQuery(collection: string, prefix: string, selected: ReturnType<typeof historySelection>): RecordQuery {
    return { collection, prefix, reverse: selected.reverse,
        ...(selected.after === undefined ? {} : { after: prefix + historyIndex(selected.after) }),
        ...(selected.before === undefined ? {} : { before: prefix + historyIndex(selected.before) }) };
}
/** Fail visibly when a custom adapter ignores range or limit fields, rather than returning incorrect pages or looping. */
export function validateHistoryRows(rows: readonly StoredRecord[], query: RecordQuery): void {
    if (query.limit !== undefined && rows.length > query.limit) throw new ProtocolError('invalid_storage', 'Store ignored the history query limit.');
    let previous: string | undefined;
    for (const row of rows) {
        if (row.collection !== query.collection || query.prefix !== undefined && !row.key.startsWith(query.prefix)
            || query.after !== undefined && row.key <= query.after || query.before !== undefined && row.key >= query.before
            || previous !== undefined && (query.reverse ? row.key >= previous : row.key <= previous))
            throw new ProtocolError('invalid_storage', 'Store did not preserve history query bounds and ordering.');
        previous = row.key;
    }
}
/** Preserve the fixed snapshot, select adjacent batches, and return each batch in ascending sequence order. */
export function historyReader<T>(reader: QueryReader<StoredRecord>, query: RecordQuery,
    project: (rows: readonly StoredRecord[], signal?: AbortSignal) => readonly T[] | Promise<readonly T[]>): QueryReader<T> {
    const gate = new AsyncGate(); const buffered: T[] = [];
    let pending: readonly StoredRecord[] | undefined; let exhausted = false; let disposed = false;
    let previous: string | undefined;
    return {
        async readNext(count, signal) {
            requireBatchCount(count);
            return gate.run(async () => {
                if (disposed) throw new ProtocolError('disposed', 'Query reader is disposed.');
                while (buffered.length < count && !exhausted) {
                    if (!pending) {
                        const size = Math.min(256, count - buffered.length);
                        pending = await reader.readNext(size, signal);
                        // Keep fetched rows on a failed/canceled projection so retrying cannot skip results.
                    }
                    validateHistoryRows(pending, { ...query, ...(previous === undefined ? {} : query.reverse ? { before: previous } : { after: previous }) });
                    if (!pending.length) { exhausted = true; pending = undefined; break; }
                    const values = await project(pending, signal);
                    buffered.push(...values); previous = pending.at(-1)!.key; pending = undefined;
                    throwIfAborted(signal);
                }
                throwIfAborted(signal);
                const batch = buffered.splice(0, count);
                return query.reverse ? batch.reverse() : batch;
            }, signal);
        },
        dispose() { return gate.run(async () => { if (disposed) return; disposed = true; buffered.length = 0; pending = undefined; await reader.dispose(); }); },
    };
}
