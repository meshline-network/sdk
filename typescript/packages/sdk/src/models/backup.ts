/** Selects optional direct or group chat history; base data is always included. */
export class BackupSelection {
    static readonly none = new BackupSelection('none', []);
    static readonly all = new BackupSelection('all', []);
    readonly ids: readonly string[];
    private constructor(readonly mode: 'none' | 'all' | 'only', ids: readonly string[]) {
        this.ids = Object.freeze([...ids]); Object.freeze(this);
    }
    /** Copies, deduplicates and ordinally sorts identifiers; an empty selection becomes none. */
    static only(ids: Iterable<string>): BackupSelection {
        if (ids === null || ids === undefined) throw new TypeError('History identifiers are required.');
        const values = [...new Set(ids)];
        if (values.some(id => typeof id !== 'string')) throw new TypeError('History identifiers must be strings.');
        return values.length ? new BackupSelection('only', values.sort()) : this.none;
    }
}

/** Both history selections default to none. Omitted history never deletes data during import. */
export interface BackupOptions {
    /** Selects private conversations by peer account ID. */
    readonly directHistory?: BackupSelection;
    /** Selects group chat history by group ID. */
    readonly groupHistory?: BackupSelection;
}
/** Counts returned on success. A failed import may still have committed earlier records. */
export interface BackupReport {
    /** Records exported, or applied to local storage during import. */
    readonly applied: number;
    /** Imported records that required no local change. */
    readonly unchanged: number;
    /** Conflicting records for which local values were retained. */
    readonly conflicts: number;
}
/** Caller-owned output. The SDK awaits each write and never closes the destination. */
export interface BackupSink { write(chunk: Uint8Array, signal?: AbortSignal): Promise<void> }
