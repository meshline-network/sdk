import type { MeshlineClient } from '../client.js';
import type { RandomSource } from '../crypto/primitives.js';
import type { BackupOptions, BackupReport, BackupSink } from '../models/backup.js';
import { readBackup, writeBackup } from '../backups/codec.js';
import { addReport, encodeRecord, failure, fields, frame, includes, integer, readSelection, record, report, selectionJson, selections, text } from '../backups/data.js';
import { canonicalJson, parseJson, requireObject, type JsonObject } from '../protocol/json.js';
import type { MeshlineStore } from '../storage/store.js';
import { createAbortController } from '../runtime/abort.js';
import { abortScope } from '../runtime/clock.js';

const owners: Readonly<Record<string, number>> = { device_state: 0, contact: 1, message: 1, group: 2, group_event: 2, group_nickname: 2, group_epoch: 2, group_secret: 2, channel: 3, end: 4 };
export async function exportClientBackup(client: MeshlineClient, store: MeshlineStore, destination: BackupSink, password: string, options: BackupOptions, random: RandomSource, createdAt: number, signal: AbortSignal): Promise<BackupReport> {
    const history = selections(options); const manifest = record('manifest', { network: client.context.toString(), account: client.accountId, created_at: createdAt, direct_history: selectionJson(history.direct), group_history: selectionJson(history.group) });
    encodeRecord(manifest); const result = report();
    await writeBackup(destination, records(), password, random, signal); return result;
    async function* records(): AsyncGenerator<JsonObject> {
        yield manifest; const states = new Map<string, string>(); const required = new Set([client.accountId]);
        const streams = [client.deviceManager.exportBackup(signal), client.messageManager.exportBackup(history.direct, signal), client.groupManager.exportBackup(history.group, signal), client.channelManager.exportBackup(signal)];
        for (const stream of streams) for await (const value of stream) {
            const data = requireObject(value.data!);
            if (value.type === 'device_state') states.set(text(data.account), canonicalJson(data));
            if (value.type === 'contact' && data.status === 'active') required.add(text(data.account));
            yield value; result.applied++;
        }
        const snapshot = await store.read([{ collection: 'device_states' }], signal);
        for (const row of snapshot.sets[0]!) {
            if (!required.has(row.key)) continue;
            if (states.get(row.key) !== canonicalJson(row.value)) failure('Device verification material changed during export. Discard output and retry.');
            required.delete(row.key);
        }
        if ([...required].some(account => states.has(account))) failure('Device verification material disappeared during export. Discard output and retry.');
        yield record('end', { records: result.applied });
    }
}
export async function importClientBackup(client: MeshlineClient, source: AsyncIterable<Uint8Array>, password: string, signal: AbortSignal): Promise<BackupReport> {
    const cancellation = createAbortController(); const scope = abortScope([signal, cancellation.signal]);
    const records = readBackup(source, password, scope.signal); const result = report();
    try {
        let next = await records.next(); if (next.done) failure('Missing manifest.'); const manifest = frame(next.value!, ['manifest']).data;
        fields(manifest, 'network account created_at direct_history group_history'); integer(manifest.created_at);
        if (manifest.network !== client.context.toString() || manifest.account !== client.accountId) failure('Backup account or network differs from the initialized database binding.');
        const direct = readSelection(manifest.direct_history, false); const groups = readSelection(manifest.group_history, true); let count = 0;
        next = await records.next(); let current = read();
        for (const [index, component] of [client.deviceManager, client.messageManager, client.groupManager, client.channelManager].entries()) addReport(result, await component.importBackup(componentRecords(index), scope.signal));
        if (current.type !== 'end') failure('Missing end record.'); fields(current.data, 'records'); if (integer(current.data.records) !== count) failure('Backup record count differs.');
        if (!(await records.next()).done) failure('Trailing backup records.'); return result;
        function read(): { type: string; data: JsonObject } { if (next.done) return failure('Missing end record.'); return frame(next.value!, Object.keys(owners)); }
        async function* componentRecords(index: number): AsyncGenerator<JsonObject> {
            while (owners[current.type] === index) {
                if (current.type === 'message' && !includes(direct, text(current.data.sender === client.accountId ? current.data.recipient : current.data.sender))) failure('Direct history is outside the manifest selection.');
                if (current.type === 'group_event' && requireObject(parseJson(text(current.data.payload_json))).$type === 'meshline.group.message' && !includes(groups, text(current.data.group_id))) failure('Group history is outside the manifest selection.');
                yield next.value!; count++; next = await records.next(); current = read();
            }
            if (owners[current.type]! < index) failure('Backup components are out of order.');
        }
    } catch (error) {
        // A component may cancel while its next record is still waiting on the source.
        cancellation.abort(error); throw error;
    } finally { try { await records.return(undefined); } finally { scope.dispose(); } }
}
