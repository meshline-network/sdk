import { ProtocolError } from '../errors.js';
import { validateRelayId } from '../identity/neo.js';
import { canonicalJson, requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';
import type { MeshlineStore, RecordKey, StoreMutation } from './store.js';
import { updateStore } from './transaction.js';

/** Exact request retained for replay and revision allocation, including after definitive rejection. */
export interface SignedRequest {
    readonly relayId: string;
    readonly revision: number;
    readonly document: JsonObject;
    /** False settles this submission; it does not by itself prove successful authorization. */
    readonly pending: boolean;
    /** Internal device-operation identity, including its explicit replacement choice. */
    readonly operationKey?: string;
}
export const requestKey = (method: string): RecordKey => ({ collection: 'signed_requests', key: method });
export function readSignedRequest(value: JsonObject | undefined): SignedRequest | undefined {
    if (value === undefined) return undefined;
    if (typeof value.relayId !== 'string' || typeof value.pending !== 'boolean' || value.document === undefined)
        throw new ProtocolError('invalid_storage', 'Invalid persisted signed request.');
    validateRelayId(value.relayId); requireSafeInteger(value.revision, 0);
    if (value.operationKey !== undefined && typeof value.operationKey !== 'string') throw new ProtocolError('invalid_storage', 'Invalid publication operation key.');
    return { relayId: value.relayId, revision: value.revision, document: requireObject(value.document), pending: value.pending,
        ...(value.operationKey === undefined ? {} : { operationKey: value.operationKey }) };
}
export function putSignedRequest(method: string, value: SignedRequest): StoreMutation {
    return { ...requestKey(method), kind: 'put', value: { ...value } };
}
/** Settle only the matching request. Persist a definitive outcome even if its caller canceled. */
export async function finishSignedRequest(store: MeshlineStore, method: string, expected: SignedRequest): Promise<void> {
    await updateStore(store, [requestKey(method)], snapshot => {
        const current = readSignedRequest(snapshot.sets[0]![0]?.value);
        const matches = current?.pending && current.relayId === expected.relayId && canonicalJson(current.document) === canonicalJson(expected.document);
        return { mutations: matches ? [putSignedRequest(method, { ...current, pending: false })] : [], result: undefined };
    });
}
