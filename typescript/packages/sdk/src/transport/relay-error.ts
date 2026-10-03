import { ProtocolError } from '../errors.js';
import { defineCodec, text, type ExtensibleModel } from '../protocol/codec.js';
import { requireObject, requireSafeInteger, type JsonObject } from '../protocol/json.js';

export interface RelayFailure extends ExtensibleModel { readonly code: string; readonly message: string; readonly data?: JsonObject }
export const relayFailureCodec = defineCodec<RelayFailure>({
    code: { wire: 'code', codec: text }, message: { wire: 'message', codec: text },
    data: { wire: 'data', codec: { decode: requireObject, encode: requireObject }, optional: true },
});

export const rpcErrorCodes: Readonly<Record<string, number>> = Object.freeze({
    bad_request: -32602, method_not_found: -32601, internal_error: -32603, unauthorized: -32001, forbidden: -32003,
    invalid_signature: -32004, device_unknown: -32005, clock_skew: -32006, not_found: -32010, state_conflict: -32011,
    stale_state: -32012, invalid_state: -32013, message_expired: -32014, request_too_large: -32015, target_not_local: -32020,
    route_stale: -32021, route_not_found: -32022, bad_gateway: -32023, rate_limited: -32030, temporarily_unavailable: -32031,
});
const definitiveCodes = new Set(Object.keys(rpcErrorCodes).filter(code => !['internal_error', 'bad_gateway', 'temporarily_unavailable'].includes(code)));

export function retryAfter(failure: RelayFailure): number | undefined {
    if (failure.code !== 'rate_limited' || failure.data?.retry_after === undefined) return undefined;
    requireSafeInteger(failure.data.retry_after, 0);
    return failure.data.retry_after;
}

/** Only isDefinitiveRejection proves that a business operation was not accepted. */
export class RelayError extends Error {
    override readonly name = 'RelayError';
    readonly failure: RelayFailure;
    constructor(failure: RelayFailure, readonly httpStatus?: number, readonly rpcCode?: number) {
        super(failure.message);
        this.failure = relayFailureCodec.decode(relayFailureCodec.encode(failure));
        retryAfter(this.failure);
    }
    get code(): string { return this.failure.code; }
    get retryAfter(): number | undefined { return retryAfter(this.failure); }
    get isDefinitiveRejection(): boolean { return definitiveCodes.has(this.code); }
}

export function validateRetryAfterHeader(failure: RelayFailure, status: number, header: string | null): void {
    if (failure.code !== 'rate_limited') return;
    const seconds = retryAfter(failure);
    if (header !== null && (seconds === undefined || !/^[0-9]+$/.test(header) || /\s/.test(header)
        || !Number.isSafeInteger(Number(header)) || Number(header) !== seconds)
        || status === 429 && seconds !== undefined && header === null)
        throw new ProtocolError('invalid_retry_after', 'Retry-After must match the error retry_after value.');
}
