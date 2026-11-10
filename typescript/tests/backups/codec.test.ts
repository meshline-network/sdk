import { readFileSync } from 'node:fs';
import { gunzipSync, gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { expect, test } from 'vitest';
import { concatBytes, encodeUtf8, systemRandom, type JsonObject } from '@meshline/sdk';
import { derivePassword, readBackup, writeBackup } from '../../packages/sdk/dist/backups/codec.js';
import { encodeRecord, parseBackupJson } from '../../packages/sdk/dist/backups/data.js';
import { ByteReader, compress, decryptStream, encryptStream, segmentSize } from '../../packages/sdk/dist/backups/streams.js';
import { hex, unhex, vector, vectorDirectory } from '../support/vectors.js';

const backup = vector<{ backup_base64: string; password: string; records_utf8: string[]; password_vectors: { password: string; salt_hex: string; key_hex: string }[] }>('backup');
const tink = JSON.parse(readFileSync(new URL('tink-streaming-aead.json', vectorDirectory), 'utf8')) as { key_hex: string; associated_data_hex: string; cases: { id: string; ciphertext_base64: string; plaintext_sha256: string }[] };
async function* chunks(value: Uint8Array, size = 8191): AsyncGenerator<Uint8Array> { for (let at = 0; at < value.length; at += size) yield value.subarray(at, at + size); }
async function collect(source: AsyncIterable<Uint8Array>): Promise<Uint8Array> { const result: Uint8Array[] = []; for await (const chunk of source) result.push(chunk.slice()); return concatBytes(...result); }
async function* records(values: JsonObject[]): AsyncGenerator<JsonObject> { yield* values; }

test.each(['synchronous', 'asynchronous'] as const)('byte reader observes %s cleanup failure after cancellation, and propagates it without cancellation', async mode => {
    const controller = new AbortController(); const reason = new Error('caller canceled'); const failure = new Error('source cleanup failed'); let closed = 0;
    const source: AsyncIterable<Uint8Array> = { [Symbol.asyncIterator]: () => ({ next: async () => ({ done: true, value: undefined }), return: () => {
        closed++; if (mode === 'synchronous') throw failure; return Promise.reject(failure);
    } }) };
    const unhandled: unknown[] = []; const observe = (error: unknown) => { unhandled.push(error); }; process.on('unhandledRejection', observe);
    try {
        controller.abort(reason); await expect(new ByteReader(source, controller.signal).close()).rejects.toBe(reason);
        await expect(new ByteReader(source).close()).rejects.toBe(failure);
        await new Promise(resolve => setImmediate(resolve)); expect(closed).toBe(2); expect(unhandled).toEqual([]);
    } finally { process.off('unhandledRejection', observe); }
});

test('byte reader cancellation abandons stalled cleanup and observes its later failure', async () => {
    const controller = new AbortController(); const reason = new Error('caller canceled');
    let entered!: () => void; const started = new Promise<void>(resolve => { entered = resolve; });
    let reject!: (error: Error) => void; const pending = new Promise<IteratorResult<Uint8Array>>((_resolve, fail) => { reject = fail; });
    const source: AsyncIterable<Uint8Array> = { [Symbol.asyncIterator]: () => ({ next: async () => ({ done: true, value: undefined }), return: () => { entered(); return pending; } }) };
    const unhandled: unknown[] = []; const observe = (error: unknown) => { unhandled.push(error); }; process.on('unhandledRejection', observe);
    try {
        const closed = expect(new ByteReader(source, controller.signal).close()).rejects.toBe(reason);
        await started; controller.abort(reason); await closed;
        reject(new Error('source cleanup failed')); await new Promise(resolve => setImmediate(resolve)); expect(unhandled).toEqual([]);
    } finally { process.off('unhandledRejection', observe); }
});
test.each(backup.password_vectors)('scrypt matches independent NFC vector $normalized_utf8_hex', async value => {
    expect(hex(await derivePassword(value.password, unhex(value.salt_hex)))).toBe(value.key_hex);
}, 30_000);
test.each(['', '\ud800', '\udfff'])('rejects invalid password %j', async password => { await expect(derivePassword(password, new Uint8Array(16))).rejects.toThrow(); });
test.each(tink.cases)('reads official Tink $id vector with short reads', async value => {
    const decoded = await collect(decryptStream(new ByteReader(chunks(Buffer.from(value.ciphertext_base64, 'base64'))), unhex(tink.key_hex), unhex(tink.associated_data_hex)));
    expect(createHash('sha256').update(decoded).digest('hex')).toBe(value.plaintext_sha256);
});
test.each([0, 1, segmentSize - 57, segmentSize - 56, segmentSize - 55, segmentSize * 2 - 72, segmentSize * 2 - 71])('authenticates segment boundaries for %i bytes', async size => {
    const source = Uint8Array.from({ length: size }, (_, i) => i % 251); const key = new Uint8Array(32).fill(8); const aad = encodeUtf8('binding');
    const encrypted = await collect(encryptStream(chunks(source), key, aad, systemRandom));
    expect(await collect(decryptStream(new ByteReader(chunks(encrypted, 113)), key, aad))).toEqual(source);
    encrypted[encrypted.length - 1]! ^= 1; await expect(collect(decryptStream(new ByteReader(chunks(encrypted)), key, aad))).rejects.toThrow();
});
test('reads the independent complete backup vector', async () => {
    const result: JsonObject[] = []; for await (const record of readBackup(chunks(Buffer.from(backup.backup_base64, 'base64'), 7), backup.password)) result.push(record);
    expect(result).toEqual(backup.records_utf8.map(value => JSON.parse(value)));
}, 30_000);
test('writes a complete gzip/encryption stream and rejects wrong passwords, tampering and truncation', async () => {
    const output: Uint8Array[] = []; const values = backup.records_utf8.map(value => JSON.parse(value) as JsonObject);
    await writeBackup({ write: async chunk => { output.push(chunk.slice()); } }, records(values), backup.password, systemRandom);
    const encoded = concatBytes(...output); const read = async (value: Uint8Array, password = backup.password) => { const result = []; for await (const row of readBackup(chunks(value), password)) result.push(row); return result; };
    expect(await read(encoded)).toEqual(values); await expect(read(encoded, 'wrong')).rejects.toThrow(); await expect(read(encoded.slice(0, -1))).rejects.toThrow();
    const changed = encoded.slice(); changed[changed.length - 1]! ^= 1; await expect(read(changed)).rejects.toThrow(); await expect(read(concatBytes(encoded, new Uint8Array([0])))).rejects.toThrow();
}, 30_000);
test('streams gzip in both directions across repeated dictionary references', async () => {
    const source = encodeUtf8('大量重复正文abc'.repeat(150_000));
    expect(gunzipSync(await collect(compress(chunks(source, 127), false)))).toEqual(Buffer.from(source));
    expect(await collect(compress(chunks(gzipSync(source), 17), true))).toEqual(source);
});
test.each(['{"a":1,"a":2}', '{"a":1.5}', '{"a":9007199254740993}', '{"a":null}x', '{"a":"\\ud800"}', '{"a":NaN}'])('rejects invalid JSON %s', source => { expect(() => parseBackupJson(encodeUtf8(source))).toThrow(); });
test('uses the four MiB record limit independently of encryption segment size', () => {
    expect(encodeRecord({ text: 'x'.repeat(2_100_000) }).length).toBeGreaterThan(segmentSize);
    expect(() => encodeRecord({ text: 'x'.repeat(4_194_304) })).toThrow('4 MiB');
});
