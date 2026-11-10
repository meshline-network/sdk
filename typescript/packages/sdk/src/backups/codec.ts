import { scryptAsync } from '@noble/hashes/scrypt.js';
import type { RandomSource } from '../crypto/primitives.js';
import type { BackupSink } from '../models/backup.js';
import { concatBytes, encodeUtf8, equalBytes } from '../protocol/encoding.js';
import { requireObject, type JsonObject } from '../protocol/json.js';
import { awaitWithSignal, throwIfAborted } from '../runtime/clock.js';
import { base64, bytes, encodeRecord, failure, fields, maximumRecord, parseBackupJson } from './data.js';
import { ByteReader, compress, decryptStream, encryptStream } from './streams.js';

const magic = encodeUtf8('MLBK0001');
function lengthBytes(length: number): Uint8Array { const bytes = new Uint8Array(4); new DataView(bytes.buffer).setUint32(0, length); return bytes; }
async function length(reader: ByteReader, maximum: number): Promise<number> {
    const raw = await reader.read(4); const count = new DataView(raw.buffer, raw.byteOffset, raw.byteLength).getUint32(0);
    if (!count || count > maximum) failure('Invalid backup frame length.'); return count;
}
export async function derivePassword(password: string, salt: Uint8Array, signal?: AbortSignal): Promise<Uint8Array> {
    if (typeof password !== 'string' || !password.length) throw new TypeError('A nonempty backup password is required.');
    const encoded = encodeUtf8(password.normalize('NFC'));
    try { throwIfAborted(signal); return await scryptAsync(encoded, salt, { N: 65536, r: 8, p: 2, dkLen: 32, maxmem: 128 * 1024 * 1024, onProgress: () => throwIfAborted(signal) }); }
    finally { encoded.fill(0); }
}
export async function writeBackup(destination: BackupSink, records: AsyncIterable<JsonObject>, password: string, random: RandomSource, signal?: AbortSignal): Promise<void> {
    const salt = random.bytes(16); const key = await derivePassword(password, salt, signal);
    try {
        const header = encodeRecord({ key_derivation: { algorithm: 'scrypt', salt: base64(salt), n: 65536, r: 8, p: 2 } });
        const preamble = concatBytes(magic, lengthBytes(header.length), header);
        await awaitWithSignal(destination.write(preamble, signal), signal);
        for await (const chunk of encryptStream(compress(framed(), false, signal), key, preamble, random, signal))
            await awaitWithSignal(destination.write(chunk, signal), signal);
    } finally { key.fill(0); }
    async function* framed(): AsyncGenerator<Uint8Array> {
        for await (const record of records) { throwIfAborted(signal); const data = encodeRecord(record); try { yield lengthBytes(data.length); yield data; } finally { data.fill(0); } }
    }
}
export async function* readBackup(source: AsyncIterable<Uint8Array>, password: string, signal?: AbortSignal): AsyncGenerator<JsonObject> {
    if (typeof password !== 'string' || !password.length) throw new TypeError('A nonempty backup password is required.');
    const sourceReader = new ByteReader(source, signal); let key: Uint8Array | undefined; let records: ByteReader | undefined;
    try {
        if (!equalBytes(await sourceReader.read(8), magic)) failure('Unsupported backup format or version.');
        const headerBytes = await sourceReader.read(await length(sourceReader, 65536)); const header = parseBackupJson(headerBytes); fields(header, 'key_derivation');
        const parameters = requireObject(header.key_derivation!); fields(parameters, 'algorithm salt n r p');
        if (parameters.algorithm !== 'scrypt' || parameters.n !== 65536 || parameters.r !== 8 || parameters.p !== 2) failure('Unsupported backup key derivation profile.');
        const salt = bytes(parameters.salt, 16); key = await derivePassword(password, salt, signal);
        const preamble = concatBytes(magic, lengthBytes(headerBytes.length), headerBytes);
        records = new ByteReader(compress(decryptStream(sourceReader, key, preamble, signal), true, signal), signal);
        for (;;) {
            const data = await records.read(await length(records, maximumRecord)); let record: JsonObject;
            try { record = parseBackupJson(data); } finally { data.fill(0); }
            yield record;
            if (record.type === 'end') {
                if ((await records.read(1, true)).length) failure('Unexpected decoded data after the backup end marker.');
                // The decompressor is driven through input EOF, authenticating every encrypted segment.
                return;
            }
        }
    } finally { key?.fill(0); try { await records?.close(); } finally { await sourceReader.close(); } }
}
