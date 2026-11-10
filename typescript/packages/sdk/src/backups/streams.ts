import { Gunzip, Gzip } from 'fflate';
import { decryptAes, deriveKey, encryptAes, requireLength, type RandomSource } from '../crypto/primitives.js';
import { concatBytes } from '../protocol/encoding.js';
import { awaitWithSignal, throwIfAborted } from '../runtime/clock.js';
import { failure } from './data.js';

export const segmentSize = 1_048_576;
const empty = new Uint8Array();
/** Pulls exactly the requested bytes, including from sources that return arbitrarily short chunks. */
export class ByteReader {
    readonly #iterator: AsyncIterator<Uint8Array>;
    #chunk: Uint8Array = empty;
    #position = 0;
    #ended = false;
    constructor(source: AsyncIterable<Uint8Array>, readonly signal?: AbortSignal) { this.#iterator = source[Symbol.asyncIterator](); }
    async read(count: number, allowEnd = false): Promise<Uint8Array> {
        throwIfAborted(this.signal); const result = new Uint8Array(count); let written = 0;
        while (written < count) {
            if (this.#position === this.#chunk.length) {
                if (this.#ended) break;
                const item = await awaitWithSignal(Promise.resolve(this.#iterator.next()), this.signal);
                if (item.done) { this.#ended = true; this.#chunk = empty; break; }
                if (!(item.value instanceof Uint8Array)) failure('Backup source must yield byte arrays.');
                this.#chunk = item.value; this.#position = 0; continue;
            }
            const length = Math.min(count - written, this.#chunk.length - this.#position);
            result.set(this.#chunk.subarray(this.#position, this.#position + length), written); this.#position += length; written += length;
        }
        if (written !== count && !allowEnd) { result.fill(0); failure('Truncated backup stream.'); }
        return written === count ? result : result.slice(0, written);
    }
    async close(): Promise<void> {
        if (this.#iterator.return) await awaitWithSignal(Promise.resolve().then(() => this.#iterator.return!()), this.signal);
    }
}
function nonce(prefix: Uint8Array, index: number, final: boolean): Uint8Array {
    if (index > 0xffff_ffff || !final && index === 0xffff_ffff) failure('Too many backup encryption segments.');
    const result = new Uint8Array(12); result.set(prefix); new DataView(result.buffer).setUint32(7, index); result[11] = final ? 1 : 0; return result;
}

/** Tink AES256_GCM_HKDF_1MB, RAW, first-segment offset zero. */
export async function* encryptStream(source: AsyncIterable<Uint8Array>, key: Uint8Array, associatedData: Uint8Array, random: RandomSource, signal?: AbortSignal): AsyncGenerator<Uint8Array> {
    requireLength(key, 32, 'Backup key'); const header = random.bytes(40); requireLength(header, 40, 'Streaming header'); header[0] = 40;
    const fileKey = deriveKey(key, header.subarray(1, 33), associatedData); const prefix = header.subarray(33);
    const buffer = new Uint8Array(segmentSize - 16); let capacity = segmentSize - 56; let used = 0; let index = 0;
    try {
        throwIfAborted(signal); yield header;
        for await (const chunk of source) {
            throwIfAborted(signal); let offset = 0;
            while (offset < chunk.length) {
                if (used === capacity) { yield encryptAes(fileKey, nonce(prefix, index++, false), buffer.subarray(0, used), empty); buffer.fill(0); used = 0; capacity = buffer.length; }
                const count = Math.min(chunk.length - offset, capacity - used); buffer.set(chunk.subarray(offset, offset + count), used); used += count; offset += count;
                throwIfAborted(signal);
            }
        }
        throwIfAborted(signal); yield encryptAes(fileKey, nonce(prefix, index, true), buffer.subarray(0, used), empty);
    } finally { fileKey.fill(0); buffer.fill(0); }
}
export async function* decryptStream(reader: ByteReader, key: Uint8Array, associatedData: Uint8Array, signal?: AbortSignal): AsyncGenerator<Uint8Array> {
    requireLength(key, 32, 'Backup key'); const header = await reader.read(40); if (header[0] !== 40) failure('Invalid backup encryption header.');
    const fileKey = deriveKey(key, header.subarray(1, 33), associatedData); const prefix = header.subarray(33);
    let capacity = segmentSize - 40; let lookahead: Uint8Array = empty; let index = 0;
    try {
        for (;;) {
            throwIfAborted(signal); const ciphertext = concatBytes(lookahead, await reader.read(capacity + 1 - lookahead.length, true));
            const final = ciphertext.length <= capacity; const length = final ? ciphertext.length : capacity;
            if (length < 16 || index > 0 && length === 16) failure('Missing or empty backup encryption segment.');
            lookahead = final ? empty : ciphertext.slice(capacity);
            const plaintext = decryptAes(fileKey, nonce(prefix, index++, final), ciphertext.subarray(0, length), empty);
            try { yield plaintext; } finally { plaintext.fill(0); }
            if (final) return;
            capacity = segmentSize;
        }
    } finally { fileKey.fill(0); }
}

/** Small pushes bound expansion and yield between compression work and caller I/O. */
export async function* compress(source: AsyncIterable<Uint8Array>, decompress: boolean, signal?: AbortSignal): AsyncGenerator<Uint8Array> {
    const output: Uint8Array[] = [];
    const callback = (data: Uint8Array): void => { if (data.length) output.push(data); };
    const stream = decompress ? new Gunzip(callback) : new Gzip({ mtime: 0 }, callback);
    try {
        for await (const chunk of source) {
            for (let offset = 0; offset < chunk.length; offset += 4096) {
                throwIfAborted(signal); stream.push(chunk.subarray(offset, offset + 4096), false);
                while (output.length) { const next = output.shift()!; try { yield next; } finally { next.fill(0); } }
            }
        }
        throwIfAborted(signal); stream.push(empty, true);
        while (output.length) { const next = output.shift()!; try { yield next; } finally { next.fill(0); } }
    } finally { for (const chunk of output) chunk.fill(0); }
}
