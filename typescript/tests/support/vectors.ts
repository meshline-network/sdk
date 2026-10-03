import { readFileSync } from 'node:fs';

export const vectorDirectory = new URL('../../../tests/vectors/', import.meta.url);

/** Loads immutable protocol fixtures; this helper never derives expected values. */
export function vector<T>(topic: string): T {
    return JSON.parse(readFileSync(new URL(`${topic}-v1.json`, vectorDirectory), 'utf8')) as T;
}

export function hex(bytes: Uint8Array): string { return Buffer.from(bytes).toString('hex'); }
export function unhex(value: string): Uint8Array { return new Uint8Array(Buffer.from(value, 'hex')); }
