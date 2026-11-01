import { ProtocolError } from '../errors.js';
import { requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';

export enum PublicationRequestState { None, Prepared, Rejected, Superseded, SupersededRejected }
export interface PublicationState {
    readonly generation: number;
    readonly requestState: PublicationRequestState;
    readonly intent?: JsonObject;
    readonly nextAttemptAt: number | null;
}
export function publicationState(value: JsonObject): PublicationState {
    const generation = value.generation ?? 0;
    const requestState = value.requestState ?? (value.document || value.request || value.payload ? PublicationRequestState.Prepared : PublicationRequestState.None);
    const nextAttemptAt = value.nextAttemptAt ?? null;
    requireSafeInteger(generation, 0); requireSafeInteger(requestState, 0);
    if (requestState > PublicationRequestState.SupersededRejected) throw new ProtocolError('invalid_storage', 'Invalid publication request state.');
    if (nextAttemptAt !== null) requireSafeInteger(nextAttemptAt, 0);
    return { generation, requestState, nextAttemptAt, ...(value.intent == null ? {} : { intent: requireObject(value.intent) }) };
}
export function canReuseRequest(state: PublicationRequestState): boolean {
    return state === PublicationRequestState.Prepared || state === PublicationRequestState.Superseded;
}
export function isCurrentRequest(state: PublicationRequestState): boolean {
    return state === PublicationRequestState.Prepared || state === PublicationRequestState.Rejected;
}
export function supersedeRequest(state: PublicationRequestState): PublicationRequestState {
    return state === PublicationRequestState.Prepared ? PublicationRequestState.Superseded
        : state === PublicationRequestState.Rejected ? PublicationRequestState.SupersededRejected : state;
}
