import { readFileSync } from 'node:fs';
import type { JsonObject, JsonValue } from '@meshline/sdk';

export const vectorDirectory = new URL('../../../tests/vectors/', import.meta.url);

/** Loads immutable protocol fixtures; this helper never derives expected values. */
export function vector<T>(topic: string): T {
    return JSON.parse(readFileSync(new URL(`${topic}-v1.json`, vectorDirectory), 'utf8')) as T;
}

export function hex(bytes: Uint8Array): string { return Buffer.from(bytes).toString('hex'); }
export function unhex(value: string): Uint8Array { return new Uint8Array(Buffer.from(value, 'hex')); }

/** Resolves an explicit value or a JSON pointer within the same immutable vector file. */
export function vectorValue(root: JsonObject, record: JsonObject, name: string): JsonValue {
    if (Object.hasOwn(record, name)) return record[name]!;
    const reference = record[name + '_ref'];
    if (typeof reference !== 'string' || !reference.startsWith('/')) throw new Error(`Missing vector reference: ${name}`);
    let value: JsonValue = root;
    for (const part of reference.slice(1).split('/')) {
        value = (Array.isArray(value) ? value[Number(part)] : (value as JsonObject)[part.replace(/~1/g, '/').replace(/~0/g, '~')])!;
        if (value === undefined) throw new Error(`Unresolved vector reference: ${reference}`);
    }
    return value;
}
