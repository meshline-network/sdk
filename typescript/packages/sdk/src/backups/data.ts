import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { BackupSelection, type BackupOptions, type BackupReport } from '../models/backup.js';
import { decodeBase64Url, decodeUtf8, encodeBase64Url, encodeUtf8, validateUnicode } from '../protocol/encoding.js';
import { requireObject, requireSafeInteger, type JsonObject, type JsonValue } from '../protocol/json.js';
import { awaitWithSignal, throwIfAborted } from '../runtime/clock.js';

export const maximumRecord = 4_194_304;
export const failure = (message: string): never => { throw new ProtocolError('invalid_backup', message); };
export function fields(value: JsonObject, allowed: string): void {
    const names = new Set(allowed.split(' '));
    if (Object.keys(value).some(name => !names.has(name))) failure('Unsupported backup field.');
}
export function text(value: JsonValue | undefined): string { if (typeof value !== 'string') failure('Expected a backup string.'); return value as string; }
export function integer(value: JsonValue | undefined, minimum = 0): number { requireSafeInteger(value, minimum); return value; }
export function boolean(value: JsonValue | undefined): boolean { if (typeof value !== 'boolean') failure('Expected a backup boolean.'); return value as boolean; }
export function base64(value: Uint8Array): string { const encoded = encodeBase64Url(value).replaceAll('-', '+').replaceAll('_', '/'); return encoded + '='.repeat((4 - encoded.length % 4) % 4); }
export function bytes(value: JsonValue | undefined, length: number): Uint8Array {
    const encoded = text(value);
    if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(encoded)) failure('Expected canonical padded Base64.');
    const result = decodeBase64Url(encoded.replace(/=+$/, '').replaceAll('+', '-').replaceAll('/', '_'), length);
    if (base64(result) !== encoded) failure('Noncanonical backup Base64.');
    return result;
}
export function selection(value: BackupSelection, group: boolean): BackupSelection {
    if (!(value instanceof BackupSelection)) throw new TypeError('Expected a BackupSelection.');
    for (const id of value.ids) { if (group) validateIdentifier('group', id); else validateAccountId(id); }
    return value;
}
export function selections(options: BackupOptions = {}): { direct: BackupSelection; group: BackupSelection } {
    return { direct: selection(options.directHistory === undefined ? BackupSelection.none : options.directHistory, false),
        group: selection(options.groupHistory === undefined ? BackupSelection.none : options.groupHistory, true) };
}
export function selectionJson(value: BackupSelection): JsonObject { return { mode: value.mode, ids: [...value.ids] }; }
export function readSelection(value: JsonValue | undefined, group: boolean): BackupSelection {
    const data = requireObject(value!); fields(data, 'mode ids');
    if (!Array.isArray(data.ids) || data.ids.some(id => typeof id !== 'string')) failure('Invalid backup history identifiers.');
    const ids = data.ids as string[];
    if (!['none', 'all', 'only'].includes(text(data.mode)) || data.mode !== 'only' && ids.length) failure('Invalid history selection.');
    return selection(data.mode === 'all' ? BackupSelection.all : data.mode === 'none' ? BackupSelection.none : BackupSelection.only(ids), group);
}
export function includes(value: BackupSelection, id: string): boolean { return value.mode === 'all' || value.mode === 'only' && value.ids.includes(id); }
export type MutableReport = { -readonly [K in keyof BackupReport]: BackupReport[K] };
export const report = (): MutableReport => ({ applied: 0, unchanged: 0, conflicts: 0 });
export function addReport(target: MutableReport, value: BackupReport): void { target.applied += value.applied; target.unchanged += value.unchanged; target.conflicts += value.conflicts; }
export type Outcome = keyof BackupReport;
export const record = (type: string, data: JsonObject): JsonObject => ({ type, data });
export async function* cancellableRecords(source: AsyncIterable<JsonObject>, signal: AbortSignal): AsyncGenerator<JsonObject> {
    const iterator = source[Symbol.asyncIterator]();
    try {
        for (;;) {
            const next = await awaitWithSignal(Promise.resolve().then(() => { throwIfAborted(signal); return iterator.next(); }), signal);
            if (next.done) return; yield next.value;
        }
    } finally { if (iterator.return) await awaitWithSignal(Promise.resolve().then(() => iterator.return!()), signal); }
}
export function frame(value: JsonObject, kinds: readonly string[]): { type: string; data: JsonObject } {
    fields(value, 'type data'); const type = text(value.type); const data = requireObject(value.data!);
    if (!kinds.includes(type)) failure('Unsupported backup record: ' + type);
    encodeRecord(value);
    return { type, data };
}

/** Backup records permit 32 levels, independently of the protocol object's own depth limit. */
export function parseBackupJson(input: Uint8Array): JsonObject {
    const source = decodeUtf8(input); let position = 0;
    const whitespace = (): void => { while (position < source.length && /[\x20\t\r\n]/.test(source[position]!)) position++; };
    const string = (): string => {
        const start = position++;
        while (position < source.length) {
            const next = source[position++];
            if (next === '"') { const value: unknown = JSON.parse(source.slice(start, position)); validateUnicode(value as string); return value as string; }
            if (next === '\\') position++;
        }
        return failure('Unterminated backup string.');
    };
    const value = (depth: number): JsonValue => {
        whitespace(); if (depth > 32) failure('Backup JSON exceeds 32 levels.');
        const next = source[position];
        if ((next === '{' || next === '[') && depth >= 32) failure('Backup JSON exceeds 32 levels.');
        if (next === '"') return string();
        if (next === '{') {
            position++; const result: JsonObject = Object.create(null) as JsonObject; whitespace();
            if (source[position] === '}') { position++; return result; }
            for (;;) {
                whitespace(); if (source[position] !== '"') failure('Expected a backup property name.'); const name = string();
                if (Object.hasOwn(result, name)) failure('Duplicate backup JSON property.');
                whitespace(); if (source[position++] !== ':') failure('Expected a colon.'); result[name] = value(depth + 1); whitespace();
                const separator = source[position++]; if (separator === '}') return result; if (separator !== ',') failure('Expected an object separator.');
            }
        }
        if (next === '[') {
            position++; const result: JsonValue[] = []; whitespace(); if (source[position] === ']') { position++; return result; }
            for (;;) { result.push(value(depth + 1)); whitespace(); const separator = source[position++]; if (separator === ']') return result; if (separator !== ',') failure('Expected an array separator.'); }
        }
        for (const [literal, result] of [['true', true], ['false', false], ['null', null]] as const) if (source.startsWith(literal, position)) { position += literal.length; return result; }
        const start = position; while (position < source.length && /[0-9eE+.\-]/.test(source[position]!)) position++;
        const token = source.slice(start, position); if (!/^-?(0|[1-9][0-9]*)$/.test(token)) failure('Expected an integer.');
        // Check the exact token before converting, so no incoming integer can be rounded silently.
        const exact = BigInt(token); if (exact < BigInt(Number.MIN_SAFE_INTEGER) || exact > BigInt(Number.MAX_SAFE_INTEGER)) failure('Backup integer exceeds the SDK safe-integer domain.');
        return Number(token);
    };
    const result = requireObject(value(0)); whitespace(); if (position !== source.length) failure('Trailing backup JSON data.'); return result;
}
export function encodeRecord(value: JsonObject): Uint8Array {
    const encoded = encodeUtf8(JSON.stringify(value));
    if (!encoded.length || encoded.length > maximumRecord) failure('Backup record exceeds 4 MiB.');
    parseBackupJson(encoded); return encoded;
}
